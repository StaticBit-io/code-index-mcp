using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class CodeIndexServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-service-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A minimal file that yields exactly one type chunk and one method chunk — the
    /// same shape used by <c>IndexBuilderTests.MakeFile</c>.</summary>
    private static string MakeSimpleFile(string ns, string className, string methodName) => $$"""
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

    /// <summary>A file whose method body is long enough that the method chunk's line range
    /// exceeds the 15-line excerpt cap. Returns both the file content and the exact source lines
    /// used to build it, so tests can compute the expected excerpt/body independently of any
    /// production code path.</summary>
    private static (string Content, IReadOnlyList<string> Lines) MakeBigMethodFile(
        string ns, string className, string methodName, int bodyStatementCount)
    {
        List<string> lines =
        [
            $"namespace {ns}",
            "{",
            $"    public class {className}",
            "    {",
            $"        public int {methodName}()",
            "        {",
        ];

        for (int i = 0; i < bodyStatementCount; i++)
        {
            lines.Add($"            int x{i} = {i};");
        }

        lines.Add("            return 0;");
        lines.Add("        }");
        lines.Add("    }");
        lines.Add("}");

        return (SourceLines.Join(lines), lines);
    }

    private CodeIndexService CreateService(
        ISourceProvider source, IEmbeddingClient embedder, out IndexStore store, string? subDirectory = null)
    {
        string directory = subDirectory is null ? _dir : Path.Combine(_dir, subDirectory);
        store = new IndexStore(directory);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        CodeIndexOptions options = new();
        IndexBuilder builder = new(source, pipeline, embedder, store, Options.Create(options));
        return new CodeIndexService(builder, source, embedder);
    }

    [Fact]
    public async Task SearchWithStatusAsync_FindsChunkByExactSymbolEvenWhenEmbeddingsCarryNoUsefulSignal()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
            ["src/C.cs"] = MakeSimpleFile("Acme.C", "Thing", "DoC"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "DoB", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.Equal("Acme.B.Gadget.DoB", result.Hits[0].Chunk.Symbol);
    }

    [Fact]
    public async Task SearchWithStatusAsync_PopulatesExcerptCappedAtFifteenLines()
    {
        (string content, IReadOnlyList<string> lines) = MakeBigMethodFile("Acme.Big", "Widget", "BigMethod", bodyStatementCount: 20);
        InMemorySourceProvider source = new(new Dictionary<string, string> { ["src/Big.cs"] = content });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "BigMethod", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        SearchHit hit = Assert.Single(result.Hits, h => h.Chunk.Symbol.EndsWith("BigMethod", StringComparison.Ordinal));

        int fullLineCount = hit.Chunk.EndLine - hit.Chunk.StartLine + 1;
        Assert.True(fullLineCount > 15, "fixture must produce a chunk longer than the excerpt cap");

        string[] excerptLines = SourceLines.Split(hit.Excerpt);
        Assert.Equal(15, excerptLines.Length);

        string[] expectedLines = lines.Skip(hit.Chunk.StartLine - 1).Take(15).ToArray();
        Assert.Equal(expectedLines, excerptLines);
    }

    [Fact]
    public async Task SearchWithStatusAsync_FiltersByKind()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "Do", limit: 10, kind: ChunkKind.Method, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Equal(ChunkKind.Method, h.Chunk.Kind));
    }

    [Fact]
    public async Task SearchWithStatusAsync_FiltersByPathFilter()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "Do", limit: 10, kind: null, pathFilter: "B.cs", TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Equal("src/B.cs", h.Chunk.FilePath));
    }

    [Fact]
    public async Task SearchWithStatusAsync_DegradesToSymbolBranchWhenEmbeddingsAreUnavailable()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
        });
        SwitchableEmbeddingClient embedder = new();
        CodeIndexService service = CreateService(source, embedder, out _);

        // Build the index while embeddings are available, as if Ollama was up at index time.
        await service.RefreshAsync(TestContext.Current.CancellationToken);

        // Ollama goes down before the query's own embedding call.
        embedder.ShouldThrow = true;

        SearchResult result = await service.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.True(result.EmbeddingsUnavailable);
        Assert.False(string.IsNullOrEmpty(result.Warning));
        Assert.NotEmpty(result.Hits);
        Assert.Equal("Acme.A.Widget.DoA", result.Hits[0].Chunk.Symbol);
    }

    [Fact]
    public async Task GetChunkAsync_ReturnsFullBodyForValidIdAndNullForOutOfRange()
    {
        (string content, IReadOnlyList<string> lines) = MakeBigMethodFile("Acme.Big", "Widget", "BigMethod", bodyStatementCount: 20);
        InMemorySourceProvider source = new(new Dictionary<string, string> { ["src/Big.cs"] = content });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IndexSnapshot snapshot = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int methodChunkId = -1;
        for (int i = 0; i < snapshot.Chunks.Count; i++)
        {
            if (snapshot.Chunks[i].Kind == ChunkKind.Method)
            {
                methodChunkId = i;
                break;
            }
        }

        Assert.True(methodChunkId >= 0);
        CodeChunk chunk = snapshot.Chunks[methodChunkId];
        int fullLineCount = chunk.EndLine - chunk.StartLine + 1;
        Assert.True(fullLineCount > 15, "fixture must produce a chunk longer than the excerpt cap");

        int generation = snapshot.Header.Generation;
        SearchHit? hit = await service.GetChunkAsync(generation, methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        string[] expectedLines = lines.Skip(chunk.StartLine - 1).Take(fullLineCount).ToArray();
        Assert.Equal(expectedLines, SourceLines.Split(hit!.Excerpt));

        SearchHit? outOfRange = await service.GetChunkAsync(generation, snapshot.Chunks.Count + 100, TestContext.Current.CancellationToken);
        Assert.Null(outOfRange);

        SearchHit? negative = await service.GetChunkAsync(generation, -1, TestContext.Current.CancellationToken);
        Assert.Null(negative);
    }

    [Fact]
    public async Task GetChunkAsync_WrongGeneration_ThrowsStaleChunkIdExceptionRatherThanResolvingTheOrdinal()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IndexSnapshot snapshot = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int wrongGeneration = snapshot.Header.Generation + 1;

        StaleChunkIdException error = await Assert.ThrowsAsync<StaleChunkIdException>(
            () => service.GetChunkAsync(wrongGeneration, 0, TestContext.Current.CancellationToken));

        Assert.Equal(wrongGeneration, error.RequestedGeneration);
        Assert.Equal(snapshot.Header.Generation, error.CurrentGeneration);
        Assert.Contains("older", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetChunkAsync_GenerationBumpsAfterAFileIsAdded_SoTheOldGenerationIsRejected()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IndexSnapshot before = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int staleGeneration = before.Header.Generation;

        // Adding a file changes the chunk list's shape — exactly what the generation counter
        // exists to flag (see IndexBuilder's HasChunkListShapeChanged).
        source.Set("src/AAA.cs", MakeSimpleFile("Acme.AAA", "Earlier", "DoEarlier"));

        IndexSnapshot after = await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(after.Header.Generation > staleGeneration);

        await Assert.ThrowsAsync<StaleChunkIdException>(
            () => service.GetChunkAsync(staleGeneration, 0, TestContext.Current.CancellationToken));

        // The current generation still resolves normally.
        SearchHit? hit = await service.GetChunkAsync(after.Header.Generation, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
    }

    [Fact]
    public async Task SearchWithStatusAsync_ReflectsAFileEditWithoutAnExplicitRebuild()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult before = await service.SearchWithStatusAsync(
            "DoARenamed", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(before.Hits, h => h.Chunk.Symbol == "Acme.A.Widget.DoARenamed");

        source.Set("src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoARenamed"));

        SearchResult after = await service.SearchWithStatusAsync(
            "DoARenamed", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(after.Hits);
        Assert.Equal("Acme.A.Widget.DoARenamed", after.Hits[0].Chunk.Symbol);
    }

    [Fact]
    public async Task SearchWithStatusAsync_FallsBackToTheStaleIndexWhenAFileChangedButEmbeddingsAreUnavailable()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        SwitchableEmbeddingClient embedder = new();
        CodeIndexService service = CreateService(source, embedder, out _);

        // Build the index while embeddings are available.
        await service.RefreshAsync(TestContext.Current.CancellationToken);

        // A file changes (would need re-embedding on the next refresh) and then Ollama goes down.
        source.Set("src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoARenamed"));
        embedder.ShouldThrow = true;

        SearchResult result = await service.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        // The call must not throw: it degrades to the last known-good (pre-edit) snapshot.
        Assert.True(result.EmbeddingsUnavailable);
        Assert.NotNull(result.Warning);
        Assert.Contains("stale", result.Warning, StringComparison.OrdinalIgnoreCase);

        // The query embedding also fails (same throwing embedder), so both failure reasons must
        // be present, not just one silently overwriting the other.
        Assert.Contains("Semantic ranking is unavailable", result.Warning, StringComparison.Ordinal);

        // Results reflect the pre-edit content: the old symbol is still found...
        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, h => h.Chunk.Symbol == "Acme.A.Widget.DoA");

        // ...and the post-edit symbol is nowhere to be seen, because the index was never
        // actually refreshed past the point embeddings stopped working.
        SearchResult renamedSearch = await service.SearchWithStatusAsync(
            "DoARenamed", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(renamedSearch.Hits, h => h.Chunk.Symbol == "Acme.A.Widget.DoARenamed");
    }

    [Fact]
    public async Task SearchWithStatusAsync_ThrowsWhenEmbeddingsAreUnavailableAndNoIndexHasEverBeenBuilt()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        SwitchableEmbeddingClient embedder = new() { ShouldThrow = true };
        CodeIndexService service = CreateService(source, embedder, out _);

        // No prior successful build exists, so there is nothing to fall back to: this is
        // genuinely fatal and must propagate rather than silently return an empty result.
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => service.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken));

        Assert.Null(service.Current);
    }

    [Fact]
    public async Task GetChunkAsync_FallsBackToTheStaleIndexWhenAFileChangedButEmbeddingsAreUnavailable()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        SwitchableEmbeddingClient embedder = new();
        CodeIndexService service = CreateService(source, embedder, out _);

        IndexSnapshot original = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int methodChunkId = Array.FindIndex(original.Chunks.ToArray(), c => c.Kind == ChunkKind.Method);
        Assert.True(methodChunkId >= 0);

        source.Set("src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoARenamed"));
        embedder.ShouldThrow = true;

        // The refresh never actually succeeds past the pre-edit snapshot (see the fallback
        // this test targets), so the generation the caller must pass is still the pre-edit one.
        SearchHit? hit = await service.GetChunkAsync(original.Header.Generation, methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.Equal("Acme.A.Widget.DoA", hit!.Chunk.Symbol);
    }

    [Fact]
    public async Task GetChunkAsync_ThrowsWhenEmbeddingsAreUnavailableAndNoIndexHasEverBeenBuilt()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        SwitchableEmbeddingClient embedder = new() { ShouldThrow = true };
        CodeIndexService service = CreateService(source, embedder, out _);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => service.GetChunkAsync(0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_ConcurrentCallsDoNotCorruptState()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
            ["src/C.cs"] = MakeSimpleFile("Acme.C", "Thing", "DoC"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IEnumerable<Task<IReadOnlyList<SearchHit>>> tasks = Enumerable.Range(0, 20)
            .Select(_ => service.SearchAsync(
                "Do", limit: 4, kind: null, pathFilter: null, TestContext.Current.CancellationToken));

        IReadOnlyList<SearchHit>[] results = await Task.WhenAll(tasks);

        string[] expectedSymbols = results[0].Select(h => h.Chunk.Symbol).ToArray();
        Assert.All(results, r => Assert.Equal(expectedSymbols, r.Select(h => h.Chunk.Symbol).ToArray()));
    }

    [Fact]
    public async Task SearchWithStatusAsync_HonoursLimit()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeSimpleFile("Acme.B", "Gadget", "DoB"),
            ["src/C.cs"] = MakeSimpleFile("Acme.C", "Thing", "DoC"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "Do", limit: 2, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public async Task SearchWithStatusAsync_LimitZeroReturnsNothing()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "DoA", limit: 0, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SearchWithStatusAsync_NegativeLimit_ThrowsArgumentException()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchWithStatusAsync(
            "DoA", limit: -1, kind: null, pathFilter: null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SearchWithStatusAsync_BlankQuery_ThrowsArgumentExceptionRatherThanReturningArbitraryHits(string blankQuery)
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchWithStatusAsync(
            blankQuery, limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchWithStatusAsync_FreshServiceSeedsFromDisk_SoAFailingFirstRefreshStillReturnsSymbolHits()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });

        // Simulate a previous, separate process run: build the index successfully with a working
        // embedder, then discard that CodeIndexService instance entirely — only the on-disk store
        // (under `store`'s directory) survives, exactly like an MCP client's next session starting
        // a brand-new server process against an already-populated cache.
        CodeIndexService priorProcess = CreateService(source, new StubEmbeddingClient(), out IndexStore store);
        await priorProcess.RefreshAsync(TestContext.Current.CancellationToken);

        // A file changes after that last successful build, so the next refresh needs to re-embed
        // something — and a brand-new CodeIndexService is constructed against the same on-disk
        // store with an embedder that has never worked in this "process" at all (Ollama was never
        // reachable this session).
        source.Set("src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoARenamed"));
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        SwitchableEmbeddingClient deadEmbedder = new() { ShouldThrow = true };
        IndexBuilder freshBuilder = new(source, pipeline, deadEmbedder, store, Options.Create(new CodeIndexOptions()));
        CodeIndexService freshService = new(freshBuilder, source, deadEmbedder);

        // Confirms this is genuinely the "nothing loaded yet" state the fix targets, not an
        // instance that happens to already hold a snapshot some other way.
        Assert.Null(freshService.Current);

        SearchResult result = await freshService.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        // Must not throw, and must find the OLD (pre-edit, on-disk) symbol via the symbol branch —
        // proving the on-disk snapshot was loaded and used instead of the call failing outright
        // just because this instance's own in-memory history is empty.
        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, h => h.Chunk.Symbol == "Acme.A.Widget.DoA");
        Assert.True(result.EmbeddingsUnavailable);
        Assert.NotNull(result.Warning);
        Assert.Contains("stale", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetChunkAsync_FreshServiceSeedsFromDisk_SoAFailingFirstRefreshStillReturnsTheChunk()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });

        CodeIndexService priorProcess = CreateService(source, new StubEmbeddingClient(), out IndexStore store);
        IndexSnapshot original = await priorProcess.RefreshAsync(TestContext.Current.CancellationToken);
        int methodChunkId = Array.FindIndex(original.Chunks.ToArray(), c => c.Kind == ChunkKind.Method);
        Assert.True(methodChunkId >= 0);

        source.Set("src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoARenamed"));
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        SwitchableEmbeddingClient deadEmbedder = new() { ShouldThrow = true };
        IndexBuilder freshBuilder = new(source, pipeline, deadEmbedder, store, Options.Create(new CodeIndexOptions()));
        CodeIndexService freshService = new(freshBuilder, source, deadEmbedder);

        Assert.Null(freshService.Current);

        SearchHit? hit = await freshService.GetChunkAsync(
            original.Header.Generation, methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.Equal("Acme.A.Widget.DoA", hit!.Chunk.Symbol);
    }

    [Fact]
    public async Task SearchWithStatusAsync_RelevanceFloor_ExcludesAWeakVectorMatchButKeepsAStrongOne()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            // "Alpha" carries the marker that makes MarkerBasedEmbeddingClient embed it aligned
            // with every query (cosine 1.0); "Beta" carries the marker that makes it embed
            // orthogonally (cosine 0.0). Neither method name contains the query term itself, so
            // the symbol branch cannot find either of them — whatever shows up came purely from
            // the vector branch, isolating exactly what the floor is supposed to filter.
            ["src/Alpha.cs"] = MakeSimpleFile("Acme.Alpha", "Widget", MarkerBasedEmbeddingClient.RealHitMarker),
            ["src/Beta.cs"] = MakeSimpleFile("Acme.Beta", "Gadget", MarkerBasedEmbeddingClient.UnrelatedHitMarker),
        });

        MarkerBasedEmbeddingClient embedder = new();
        IndexStore store = new(_dir);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        IndexBuilder builder = new(source, pipeline, embedder, store, Options.Create(new CodeIndexOptions()));

        // A floor of 0.5 sits strictly between the two markers' cosine similarities (1.0 and 0.0),
        // so it must keep the aligned hit and drop the orthogonal one.
        CodeIndexService service = new(builder, source, embedder, minCosineSimilarity: 0.5);

        SearchResult result = await service.SearchWithStatusAsync(
            "does not matter — MarkerBasedEmbeddingClient ignores query text",
            limit: 10, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Hits, h => h.Chunk.FilePath == "src/Alpha.cs");
        Assert.DoesNotContain(result.Hits, h => h.Chunk.FilePath == "src/Beta.cs");
    }

    [Fact]
    public async Task SearchWithStatusAsync_NoRelevanceFloor_TheOtherwiseExcludedWeakMatchComesBackThrough()
    {
        // Same fixture and embedder as the test above, but with no floor configured (the test
        // default — see CodeIndexService's constructor remarks) — control case proving the
        // exclusion above is actually caused by the floor, not by some other filtering.
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/Alpha.cs"] = MakeSimpleFile("Acme.Alpha", "Widget", MarkerBasedEmbeddingClient.RealHitMarker),
            ["src/Beta.cs"] = MakeSimpleFile("Acme.Beta", "Gadget", MarkerBasedEmbeddingClient.UnrelatedHitMarker),
        });

        MarkerBasedEmbeddingClient embedder = new();
        CodeIndexService service = CreateService(source, embedder, out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "does not matter — MarkerBasedEmbeddingClient ignores query text",
            limit: 10, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Hits, h => h.Chunk.FilePath == "src/Alpha.cs");
        Assert.Contains(result.Hits, h => h.Chunk.FilePath == "src/Beta.cs");
    }

    [Fact]
    public async Task SearchWithStatusAsync_ExcerptFromAFileEditedDuringTheCall_CarriesTheStalenessSignal()
    {
        InMemorySourceProvider inMemory = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        RacyStatSourceProvider source = new(inMemory);
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        // Build the index normally first.
        await service.RefreshAsync(TestContext.Current.CancellationToken);

        // Let the upcoming search's own mandatory refresh see the file as unchanged (one normal
        // stat call), then report a stat that no longer matches the fingerprint captured just now
        // for every call after that — i.e. the one CodeIndexService performs immediately before
        // reading each hit's excerpt. This simulates an edit landing after the refresh completed
        // but before the excerpt was actually read later in the same call, the same window the
        // project's own measurements put at ~200 ms warm to ~12 s cold (the query-embedding call).
        SourceFileStat racyStat = new(Length: 99_999, LastWriteTimeUtc: DateTime.UtcNow);
        source.RaceAfter("src/A.cs", normalCallsBeforeRacing: 1, racyStat);

        SearchResult result = await service.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.True(h.ExcerptMayBeStale));

        // The excerpt is still returned despite the flag — a probably-correct excerpt with a
        // caveat, not withheld entirely.
        Assert.All(result.Hits, h => Assert.False(string.IsNullOrEmpty(h.Excerpt)));
    }

    [Fact]
    public async Task SearchWithStatusAsync_ExcerptFromAnUntouchedFile_DoesNotCarryTheStalenessSignal()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        SearchResult result = await service.SearchWithStatusAsync(
            "DoA", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.False(h.ExcerptMayBeStale));
    }

    [Fact]
    public async Task GetChunkAsync_BodyFromAFileEditedDuringTheCall_CarriesTheStalenessSignal()
    {
        InMemorySourceProvider inMemory = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        RacyStatSourceProvider source = new(inMemory);
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IndexSnapshot snapshot = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int methodChunkId = Array.FindIndex(snapshot.Chunks.ToArray(), c => c.Kind == ChunkKind.Method);
        Assert.True(methodChunkId >= 0);

        SourceFileStat racyStat = new(Length: 99_999, LastWriteTimeUtc: DateTime.UtcNow);
        source.RaceAfter("src/A.cs", normalCallsBeforeRacing: 1, racyStat);

        SearchHit? hit = await service.GetChunkAsync(
            snapshot.Header.Generation, methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.True(hit!.ExcerptMayBeStale);
        Assert.False(string.IsNullOrEmpty(hit.Excerpt));
    }

    [Fact]
    public async Task GetChunkAsync_BodyFromAnUntouchedFile_DoesNotCarryTheStalenessSignal()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeSimpleFile("Acme.A", "Widget", "DoA"),
        });
        CodeIndexService service = CreateService(source, new StubEmbeddingClient(), out _);

        IndexSnapshot snapshot = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int methodChunkId = Array.FindIndex(snapshot.Chunks.ToArray(), c => c.Kind == ChunkKind.Method);
        Assert.True(methodChunkId >= 0);

        SearchHit? hit = await service.GetChunkAsync(
            snapshot.Header.Generation, methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.False(hit!.ExcerptMayBeStale);
    }

    /// <summary>
    /// Wraps an <see cref="ISourceProvider"/> so a test can simulate a concurrent edit landing
    /// mid-call: after a configured number of legitimate <see cref="StatAsync"/> calls for a given
    /// path (typically the one a search's own mandatory refresh performs to confirm nothing
    /// changed), every subsequent call for that path returns a fixed, mismatched stat instead of
    /// delegating — modelling the real race the "excerpt may be stale" signal exists to catch,
    /// which cannot otherwise be reproduced deterministically without real concurrent disk I/O.
    /// </summary>
    private sealed class RacyStatSourceProvider : ISourceProvider
    {
        private readonly ISourceProvider _inner;
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _remainingNormalCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceFileStat> _racyStats = new(StringComparer.Ordinal);

        public RacyStatSourceProvider(ISourceProvider inner) => _inner = inner;

        public void RaceAfter(string relativePath, int normalCallsBeforeRacing, SourceFileStat racyStat)
        {
            lock (_gate)
            {
                _remainingNormalCalls[relativePath] = normalCallsBeforeRacing;
                _racyStats[relativePath] = racyStat;
            }
        }

        public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken) =>
            _inner.EnumerateAsync(cancellationToken);

        public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken) =>
            _inner.ReadTextAsync(relativePath, cancellationToken);

        public Task<string> ReadLinesAsync(string relativePath, int startLine, int endLine, CancellationToken cancellationToken) =>
            _inner.ReadLinesAsync(relativePath, startLine, endLine, cancellationToken);

        public async Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken)
        {
            SourceFileStat? racyStat = null;
            lock (_gate)
            {
                if (_remainingNormalCalls.TryGetValue(relativePath, out int remaining) && remaining <= 0)
                {
                    racyStat = _racyStats[relativePath];
                }
                else if (_remainingNormalCalls.ContainsKey(relativePath))
                {
                    _remainingNormalCalls[relativePath] = remaining - 1;
                }
            }

            if (racyStat is not null)
            {
                return racyStat.Value;
            }

            return await _inner.StatAsync(relativePath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Behaves exactly like <see cref="StubEmbeddingClient"/> until toggled, then
    /// throws <see cref="EmbeddingUnavailableException"/> on every call — lets a test simulate
    /// Ollama going down *after* the index was already built with working embeddings.</summary>
    private sealed class SwitchableEmbeddingClient : IEmbeddingClient
    {
        private readonly StubEmbeddingClient _inner = new();

        public bool ShouldThrow { get; set; }

        public int Dimensions => _inner.Dimensions;

        public string Model => _inner.Model;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            if (ShouldThrow)
            {
                throw new EmbeddingUnavailableException("Ollama is unreachable at http://localhost:11434.");
            }

            return _inner.EmbedAsync(inputs, cancellationToken);
        }

        /// <summary>Delegates to this class's own <see cref="EmbedAsync"/> so <see
        /// cref="ShouldThrow"/> governs query embedding the same way it governs passage
        /// embedding — the tests that toggle it need the query-embedding call to fail too.</summary>
        public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<float[]> result = await EmbedAsync([query], cancellationToken).ConfigureAwait(false);
            return result[0];
        }
    }
}
