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

        return (string.Join('\n', lines), lines);
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

        string[] excerptLines = hit.Excerpt.Split('\n');
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

        SearchHit? hit = await service.GetChunkAsync(methodChunkId, TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        string[] expectedLines = lines.Skip(chunk.StartLine - 1).Take(fullLineCount).ToArray();
        Assert.Equal(expectedLines, hit!.Excerpt.Split('\n'));

        SearchHit? outOfRange = await service.GetChunkAsync(snapshot.Chunks.Count + 100, TestContext.Current.CancellationToken);
        Assert.Null(outOfRange);

        SearchHit? negative = await service.GetChunkAsync(-1, TestContext.Current.CancellationToken);
        Assert.Null(negative);
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

        SearchHit? hit = await service.GetChunkAsync(methodChunkId, TestContext.Current.CancellationToken);

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
            () => service.GetChunkAsync(0, TestContext.Current.CancellationToken));
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
