using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Indexing;

public sealed class IndexBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-builder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A minimal file that yields exactly one type chunk and one method chunk from
    /// <see cref="RoslynChunker"/> — the same shape used by <c>RoslynChunkerTests.Sample</c>.</summary>
    private static string MakeFile(string ns, string className, string methodName) => $$"""
        namespace {{ns}}
        {
            public class {{className}}
            {
                public int {{methodName}}()
                {
                    return 1;
                }
            }
        }
        """;

    private IndexBuilder CreateBuilder(
        ISourceProvider source, IEmbeddingClient embedder, out IndexStore store, string? subDirectory = null)
    {
        string directory = subDirectory is null ? _dir : Path.Combine(_dir, subDirectory);
        store = new IndexStore(directory);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        CodeIndexOptions options = new() { ProjectId = "test-project" };
        return new IndexBuilder(source, pipeline, embedder, store, Options.Create(options));
    }

    [Fact]
    public async Task BuildAsync_EmbedsEveryChunkAndPersistsTheResult()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out IndexStore store);

        IndexSnapshot snapshot = await builder.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, snapshot.Chunks.Count);
        Assert.Equal(4 * embedder.Dimensions, snapshot.Vectors.Length);
        Assert.Equal(4, embedder.TotalInputs);

        IndexSnapshot? loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Chunks.Count);
        Assert.Equal(4 * embedder.Dimensions, loaded.Vectors.Length);
    }

    [Fact]
    public async Task RefreshAsync_WithNothingChanged_DoesNotCallTheEmbedderOrRewriteTheStore()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out IndexStore store);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        int callsBefore = embedder.CallCount;
        byte[] manifestBefore = await File.ReadAllBytesAsync(store.ManifestPath, TestContext.Current.CancellationToken);
        byte[] vectorsBefore = await File.ReadAllBytesAsync(store.VectorsPath, TestContext.Current.CancellationToken);

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(callsBefore, embedder.CallCount);
        Assert.Equal(2, refreshed.Chunks.Count);

        byte[] manifestAfter = await File.ReadAllBytesAsync(store.ManifestPath, TestContext.Current.CancellationToken);
        byte[] vectorsAfter = await File.ReadAllBytesAsync(store.VectorsPath, TestContext.Current.CancellationToken);
        Assert.Equal(manifestBefore, manifestAfter);
        Assert.Equal(vectorsBefore, vectorsAfter);
    }

    [Fact]
    public async Task RefreshAsync_ReEmbedsOnlyTheChangedFile()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        int totalBefore = embedder.TotalInputs;
        int callsBefore = embedder.CallCount;
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBRenamed"));

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        // Exactly the 2 chunks (type + method) of the changed file were re-embedded — not 4.
        Assert.Equal(totalBefore + 2, embedder.TotalInputs);
        Assert.Equal(callsBefore + 1, embedder.CallCount);

        Assert.Equal(4, refreshed.Chunks.Count);
        Assert.Contains(refreshed.Chunks, c => c.Symbol == "Acme.B.Gadget.DoBRenamed");
        Assert.DoesNotContain(refreshed.Chunks, c => c.Symbol == "Acme.B.Gadget.DoB");
        Assert.Contains(refreshed.Chunks, c => c.Symbol == "Acme.A.Widget.DoA");
    }

    [Fact]
    public async Task RefreshAsync_DropsChunksOfDeletedFilesAndShrinksTheVectorArray()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        source.Remove("src/B.cs");

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, refreshed.Chunks.Count);
        Assert.All(refreshed.Chunks, c => Assert.Equal("src/A.cs", c.FilePath));
        Assert.Equal(2 * embedder.Dimensions, refreshed.Vectors.Length);
        Assert.Single(refreshed.Fingerprints);
        Assert.Equal("src/A.cs", refreshed.Fingerprints[0].RelativePath);
    }

    [Fact]
    public async Task RefreshAsync_RebuildsFullyWhenTheStoredModelDiffersFromCurrent()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out IndexStore store);
        IndexSnapshot original = await builder.BuildAsync(TestContext.Current.CancellationToken);

        IndexSnapshot doctored = original with { Header = original.Header with { Model = "some-other-model" } };
        await store.SaveAsync(doctored, TestContext.Current.CancellationToken);

        int totalBefore = embedder.TotalInputs;

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        // A full rebuild re-embeds every chunk again, not just a delta.
        Assert.Equal(totalBefore + original.Chunks.Count, embedder.TotalInputs);
        Assert.Equal(embedder.Model, refreshed.Header.Model);
        Assert.Equal(original.Chunks.Count, refreshed.Chunks.Count);
    }

    [Fact]
    public async Task RefreshAsync_GitCheckoutCase_DoesNotReEmbedButRefreshesTheFingerprintStamp()
    {
        string content = MakeFile("Acme.A", "Widget", "DoA");
        InMemorySourceProvider source = new(new Dictionary<string, string> { ["src/A.cs"] = content });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);
        IndexSnapshot original = await builder.BuildAsync(TestContext.Current.CancellationToken);
        DateTime originalStamp = original.Fingerprints[0].LastWriteTimeUtc;

        int callsBefore = embedder.CallCount;

        // Same content, but Set() always advances the timestamp — this is the `git checkout`
        // case: every timestamp moves, no content does.
        source.Set("src/A.cs", content);

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(callsBefore, embedder.CallCount);
        Assert.NotEqual(originalStamp, refreshed.Fingerprints[0].LastWriteTimeUtc);

        // Chunks and vectors themselves are untouched. original.Chunks already has EmbedText
        // stripped too — every chunk IndexBuilder returns does, whether reused or freshly
        // embedded (see the uniform-EmbedText contract on BuildAsync/RefreshAsync) — so this is
        // a direct comparison, not a workaround for a reload-only quirk.
        Assert.Equal(original.Chunks, refreshed.Chunks);
        Assert.Equal(original.Vectors, refreshed.Vectors);

        // And a second refresh with truly nothing changed must now be a no-op again.
        int callsAfterFirstRefresh = embedder.CallCount;
        await builder.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(callsAfterFirstRefresh, embedder.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_KeepsEveryChunkAlignedWithItsOwnVectorAfterAPartialRefresh()
    {
        string contentA = MakeFile("Acme.A", "Widget", "DoA");
        string contentB = MakeFile("Acme.B", "Gadget", "DoB");
        string contentC = MakeFile("Acme.C", "Thing", "DoC");

        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = contentA,
            ["src/B.cs"] = contentB,
            ["src/C.cs"] = contentC,
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        string changedContentB = MakeFile("Acme.B", "Gadget", "DoBChanged");
        source.Set("src/B.cs", changedContentB);

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, refreshed.Chunks.Count);

        // The independent oracle: re-chunk each file's *current* content directly, which yields
        // exactly the EmbedText each of its chunks should have been embedded from — regardless
        // of whether that chunk was reused from the store or freshly re-embedded this refresh.
        Dictionary<string, string> currentContentByPath = new(StringComparer.Ordinal)
        {
            ["src/A.cs"] = contentA,
            ["src/B.cs"] = changedContentB,
            ["src/C.cs"] = contentC,
        };
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        Dictionary<string, Queue<string>> expectedTextsByPath = currentContentByPath.ToDictionary(
            kv => kv.Key,
            kv => new Queue<string>(pipeline.ChunkFile(kv.Key, kv.Value).Select(c => c.EmbedText)),
            StringComparer.Ordinal);

        for (int i = 0; i < refreshed.Chunks.Count; i++)
        {
            CodeChunk chunk = refreshed.Chunks[i];
            string expectedText = expectedTextsByPath[chunk.FilePath].Dequeue();

            IReadOnlyList<float[]> expected = await embedder.EmbedAsync(
                [expectedText], TestContext.Current.CancellationToken);

            Assert.Equal(expected[0], refreshed.VectorAt(i).ToArray());
        }

        Assert.All(expectedTextsByPath.Values, queue => Assert.Empty(queue));
    }

    [Fact]
    public async Task BuildAsync_ProducesIdenticalChunkOrderAcrossTwoIndependentBuilds()
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["src/Z.cs"] = MakeFile("Acme.Z", "Zeta", "DoZ"),
            ["src/A.cs"] = MakeFile("Acme.A", "Alpha", "DoA"),
            ["src/M.cs"] = MakeFile("Acme.M", "Mid", "DoM"),
        };

        InMemorySourceProvider source1 = new(files);
        InMemorySourceProvider source2 = new(files);

        IndexBuilder builder1 = CreateBuilder(source1, new StubEmbeddingClient(), out _, subDirectory: "build1");
        IndexBuilder builder2 = CreateBuilder(source2, new StubEmbeddingClient(), out _, subDirectory: "build2");

        IndexSnapshot snapshot1 = await builder1.BuildAsync(TestContext.Current.CancellationToken);
        IndexSnapshot snapshot2 = await builder2.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            snapshot1.Chunks.Select(c => (c.FilePath, c.Symbol, c.Kind)),
            snapshot2.Chunks.Select(c => (c.FilePath, c.Symbol, c.Kind)));

        // And the files themselves must be assembled in ordinal path order: A, M, Z.
        Assert.Equal(
            ["src/A.cs", "src/A.cs", "src/M.cs", "src/M.cs", "src/Z.cs", "src/Z.cs"],
            snapshot1.Chunks.Select(c => c.FilePath));
    }

    [Fact]
    public async Task BuildAsync_FileWithZeroChunksDoesNotBreakAssemblyOrLeaveAHoleInTheVectorArray()
    {
        string contentA = MakeFile("Acme.A", "Widget", "DoA");
        string contentB = MakeFile("Acme.B", "Gadget", "DoB");

        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = contentA,
            ["src/Empty.cs"] = string.Empty,
            ["src/B.cs"] = contentB,
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);

        IndexSnapshot snapshot = await builder.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, snapshot.Chunks.Count);
        Assert.Equal(4 * embedder.Dimensions, snapshot.Vectors.Length);
        Assert.Equal(3, snapshot.Fingerprints.Count);
        Assert.DoesNotContain(snapshot.Chunks, c => c.FilePath == "src/Empty.cs");
        Assert.Contains(snapshot.Fingerprints, f => f.RelativePath == "src/Empty.cs");

        // The independent oracle: returned chunks always have EmbedText stripped (see
        // IndexBuilder's uniform-EmbedText contract), so the expected vector for each chunk is
        // recomputed directly from its file's own content rather than read back off the chunk.
        Dictionary<string, string> contentByPath = new(StringComparer.Ordinal)
        {
            ["src/A.cs"] = contentA,
            ["src/B.cs"] = contentB,
        };
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        Dictionary<string, Queue<string>> expectedTextsByPath = contentByPath.ToDictionary(
            kv => kv.Key,
            kv => new Queue<string>(pipeline.ChunkFile(kv.Key, kv.Value).Select(c => c.EmbedText)),
            StringComparer.Ordinal);

        for (int i = 0; i < snapshot.Chunks.Count; i++)
        {
            string expectedText = expectedTextsByPath[snapshot.Chunks[i].FilePath].Dequeue();
            IReadOnlyList<float[]> expected = await embedder.EmbedAsync(
                [expectedText], TestContext.Current.CancellationToken);

            Assert.Equal(expected[0], snapshot.VectorAt(i).ToArray());
        }

        Assert.All(expectedTextsByPath.Values, queue => Assert.Empty(queue));
    }

    [Fact]
    public async Task RefreshAsync_CorruptedCacheTriggersARebuildInsteadOfThrowing()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out IndexStore store);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(store.ManifestPath, "{ this is not valid json", TestContext.Current.CancellationToken);

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, refreshed.Chunks.Count);

        IndexSnapshot? reloaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Chunks.Count);
    }

    [Fact]
    public async Task RefreshAsync_WithNothingChanged_NeverReadsAnyFileContent()
    {
        // A no-op refresh must be cheap: it may Stat every file to confirm nothing changed, but
        // it must never re-read file content or decompose the stored snapshot's chunks/vectors —
        // that decomposition is what made every search-time refresh allocate and copy every
        // vector in the index for no reason.
        InMemorySourceProvider inMemory = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        ReadCountingSourceProvider counting = new(inMemory);
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(counting, embedder, out _);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        int readsBefore = counting.ReadTextCallCount;

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(readsBefore, counting.ReadTextCallCount);
        Assert.Equal(4, refreshed.Chunks.Count);
    }

    [Fact]
    public async Task RefreshAsync_WithCurrentSnapshotProvided_NeverTouchesTheOnDiskStore()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out IndexStore store);
        IndexSnapshot original = await builder.BuildAsync(TestContext.Current.CancellationToken);

        // Corrupt the on-disk store. If RefreshAsync ever fell back to IndexStore.LoadAsync here,
        // this would either throw or trigger a full rebuild-and-resave; neither may happen when
        // `current` is supplied.
        const string corruptedManifest = "{ this is not valid json";
        await File.WriteAllTextAsync(store.ManifestPath, corruptedManifest, TestContext.Current.CancellationToken);

        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken, current: original);

        Assert.Equal(2, refreshed.Chunks.Count);

        string manifestOnDisk = await File.ReadAllTextAsync(store.ManifestPath, TestContext.Current.CancellationToken);
        Assert.Equal(corruptedManifest, manifestOnDisk);
    }

    [Fact]
    public async Task BuildAsyncAndRefreshAsync_AlwaysReturnChunksWithEmptyEmbedText()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        StubEmbeddingClient embedder = new();
        IndexBuilder builder = CreateBuilder(source, embedder, out _);

        IndexSnapshot built = await builder.BuildAsync(TestContext.Current.CancellationToken);
        Assert.All(built.Chunks, c => Assert.Equal(string.Empty, c.EmbedText));

        // A partial refresh mixes reused (A, untouched) and freshly re-embedded (B, changed)
        // chunks in the same returned snapshot — both must come back normalised the same way.
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBChanged"));
        IndexSnapshot refreshed = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.All(refreshed.Chunks, c => Assert.Equal(string.Empty, c.EmbedText));
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenTheEmbeddingClientReturnsFewerVectorsThanInputs()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        MisbehavingEmbeddingClient embedder = new() { Mode = MisbehaviorMode.DropOneVector };
        IndexBuilder builder = CreateBuilder(source, embedder, out _);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => builder.BuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenTheEmbeddingClientReturnsAWrongDimensionVector()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
        });
        MisbehavingEmbeddingClient embedder = new() { Mode = MisbehaviorMode.ShortVector };
        IndexBuilder builder = CreateBuilder(source, embedder, out _);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => builder.BuildAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Wraps an <see cref="ISourceProvider"/> and counts <see cref="ReadTextAsync"/>
    /// calls, so a test can assert that a code path never reads file content (as opposed to only
    /// statting it).</summary>
    private sealed class ReadCountingSourceProvider : ISourceProvider
    {
        private readonly ISourceProvider _inner;

        public ReadCountingSourceProvider(ISourceProvider inner)
        {
            _inner = inner;
        }

        public int ReadTextCallCount { get; private set; }

        public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken) =>
            _inner.EnumerateAsync(cancellationToken);

        public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken)
        {
            ReadTextCallCount++;
            return _inner.ReadTextAsync(relativePath, cancellationToken);
        }

        public Task<string> ReadLinesAsync(string relativePath, int startLine, int endLine, CancellationToken cancellationToken) =>
            _inner.ReadLinesAsync(relativePath, startLine, endLine, cancellationToken);

        public Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken) =>
            _inner.StatAsync(relativePath, cancellationToken);
    }
}
