using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Overlays;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Overlays;

/// <summary>
/// Covers the base+overlay pool described in
/// <c>docs/superpowers/specs/2026-07-28-overlay-indexing-design.md</c>: an overlay is created for
/// a genuinely diverging state and reused (no re-embedding) when that state recurs, the base's own
/// on-disk data is never mutated by an active overlay, composition masks/hides overridden and
/// deleted paths correctly, eviction respects the configured pool size, a stale chunk id from a
/// different composition is still detected, and a project that never diverges creates no overlay
/// at all.
/// </summary>
public sealed class OverlayIndexBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-overlay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static string MakeFile(string ns, string className, string methodName, int bodyValue = 1) => $$"""
        namespace {{ns}}
        {
            public class {{className}}
            {
                public int {{methodName}}()
                {
                    return {{bodyValue}};
                }
            }
        }
        """;

    private (OverlayIndexBuilder Overlay, IndexBuilder Base, StubEmbeddingClient Embedder) CreateBuilder(
        InMemorySourceProvider source, int maxOverlays = 8, int activationThreshold = 10)
    {
        StubEmbeddingClient embedder = new();
        IndexStore store = new(_dir);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        IndexBuilder baseBuilder = new(source, pipeline, embedder, store, Options.Create(new CodeIndexOptions()));
        OverlayIndexBuilder overlay = new(baseBuilder, source, _dir, maxOverlays, activationThreshold);
        return (overlay, baseBuilder, embedder);
    }

    private string OverlaysDirectory => Path.Combine(_dir, "overlays");

    [Fact]
    public async Task ProjectThatNeverDivergesCreatesNoOverlay()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        (OverlayIndexBuilder overlay, _, _) = CreateBuilder(source, activationThreshold: 10);

        IndexSnapshot snapshot = await overlay.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(OverlaysDirectory));

        // A no-op refresh (nothing changed at all).
        IndexSnapshot again = await overlay.RefreshAsync(TestContext.Current.CancellationToken, snapshot);
        Assert.False(Directory.Exists(OverlaysDirectory));

        // A small edit, below the activation threshold: folded into base in place.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoARenamed"));
        IndexSnapshot afterSmallEdit = await overlay.RefreshAsync(TestContext.Current.CancellationToken, again);

        Assert.False(Directory.Exists(OverlaysDirectory));
        Assert.Contains(afterSmallEdit.Chunks, c => c.Symbol == "Acme.A.Widget.DoARenamed");
    }

    [Fact]
    public async Task OverlayIsCreatedForDivergingStateAndReusedOnReturn()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
            ["src/C.cs"] = MakeFile("Acme.C", "Thing", "DoC"),
            ["src/D.cs"] = MakeFile("Acme.D", "Other", "DoD"),
        });
        (OverlayIndexBuilder overlay, _, StubEmbeddingClient embedder) = CreateBuilder(source, maxOverlays: 4, activationThreshold: 2);

        IndexSnapshot mainState = await overlay.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(OverlaysDirectory));

        // "git checkout feature": 3 files (>= threshold of 2) genuinely differ from base.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoAFeature"));
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBFeature"));
        source.Set("src/C.cs", MakeFile("Acme.C", "Thing", "DoCFeature"));

        int inputsBeforeFeature = embedder.TotalInputs;
        IndexSnapshot featureState = await overlay.RefreshAsync(TestContext.Current.CancellationToken, mainState);

        Assert.True(Directory.Exists(OverlaysDirectory));
        Assert.True(embedder.TotalInputs > inputsBeforeFeature, "Switching to a genuinely new state must embed the changed files.");
        Assert.Contains(featureState.Chunks, c => c.Symbol == "Acme.A.Widget.DoAFeature");
        Assert.Contains(featureState.Chunks, c => c.Symbol == "Acme.D.Other.DoD"); // untouched file inherited from base

        // "git checkout main": revert exactly back to what base already holds.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoA"));
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoB"));
        source.Set("src/C.cs", MakeFile("Acme.C", "Thing", "DoC"));

        int inputsBeforeBackToMain = embedder.TotalInputs;
        IndexSnapshot backToMain = await overlay.RefreshAsync(TestContext.Current.CancellationToken, featureState);

        Assert.Equal(inputsBeforeBackToMain, embedder.TotalInputs); // reverting to base needs no re-embedding
        Assert.Contains(backToMain.Chunks, c => c.Symbol == "Acme.A.Widget.DoA");

        // "git checkout feature" again: this exact state was already cached.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoAFeature"));
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBFeature"));
        source.Set("src/C.cs", MakeFile("Acme.C", "Thing", "DoCFeature"));

        int inputsBeforeSecondFeatureVisit = embedder.TotalInputs;
        IndexSnapshot featureAgain = await overlay.RefreshAsync(TestContext.Current.CancellationToken, backToMain);

        Assert.Equal(inputsBeforeSecondFeatureVisit, embedder.TotalInputs); // reused, not re-embedded
        Assert.Contains(featureAgain.Chunks, c => c.Symbol == "Acme.A.Widget.DoAFeature");
        Assert.Contains(featureAgain.Chunks, c => c.Symbol == "Acme.B.Gadget.DoBFeature");
        Assert.Contains(featureAgain.Chunks, c => c.Symbol == "Acme.C.Thing.DoCFeature");
    }

    [Fact]
    public async Task BaseIsNeverMutatedWhileAnOverlayIsActive()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
            ["src/C.cs"] = MakeFile("Acme.C", "Thing", "DoC"),
        });
        (OverlayIndexBuilder overlay, _, _) = CreateBuilder(source, activationThreshold: 2);

        IndexSnapshot mainState = await overlay.RefreshAsync(TestContext.Current.CancellationToken);
        byte[] baseManifestBefore = await File.ReadAllBytesAsync(Path.Combine(_dir, "manifest.json"), TestContext.Current.CancellationToken);
        byte[] baseVectorsBefore = await File.ReadAllBytesAsync(Path.Combine(_dir, "vectors.bin"), TestContext.Current.CancellationToken);

        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoAFeature"));
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBFeature"));

        await overlay.RefreshAsync(TestContext.Current.CancellationToken, mainState);

        byte[] baseManifestAfter = await File.ReadAllBytesAsync(Path.Combine(_dir, "manifest.json"), TestContext.Current.CancellationToken);
        byte[] baseVectorsAfter = await File.ReadAllBytesAsync(Path.Combine(_dir, "vectors.bin"), TestContext.Current.CancellationToken);

        Assert.Equal(baseManifestBefore, baseManifestAfter);
        Assert.Equal(baseVectorsBefore, baseVectorsAfter);

        // The base, loaded directly and bypassing the overlay entirely, still shows main's content.
        IndexSnapshot rawBase = (await new IndexStore(_dir).LoadAsync(TestContext.Current.CancellationToken))!;
        Assert.Contains(rawBase.Chunks, c => c.Symbol == "Acme.A.Widget.DoA");
        Assert.DoesNotContain(rawBase.Chunks, c => c.Symbol == "Acme.A.Widget.DoAFeature");
    }

    [Fact]
    public async Task EvictionRemovesTheLeastRecentlyUsedSlotWhenThePoolIsFull()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        (OverlayIndexBuilder overlay, _, _) = CreateBuilder(source, maxOverlays: 1, activationThreshold: 1);

        IndexSnapshot mainState = await overlay.RefreshAsync(TestContext.Current.CancellationToken);

        // Diverge (state 1), then revert exactly to base so the next divergence starts a brand
        // new slot rather than evolving the still-active one.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoAOne"));
        IndexSnapshot state1 = await overlay.RefreshAsync(TestContext.Current.CancellationToken, mainState);
        string firstSlot = Directory.GetDirectories(OverlaysDirectory).Single();

        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoA"));
        IndexSnapshot backToMain = await overlay.RefreshAsync(TestContext.Current.CancellationToken, state1);

        // Diverge again (state 2, a different path/content) -- with maxOverlays = 1, this must
        // evict the first slot.
        source.Set("src/B.cs", MakeFile("Acme.B", "Gadget", "DoBTwo"));
        IndexSnapshot state2 = await overlay.RefreshAsync(TestContext.Current.CancellationToken, backToMain);

        Assert.False(Directory.Exists(firstSlot), "The least-recently-used overlay slot must have been evicted.");
        Assert.Contains(state2.Chunks, c => c.Symbol == "Acme.B.Gadget.DoBTwo");

        string[] remainingSlots = Directory.GetDirectories(OverlaysDirectory);
        Assert.Single(remainingSlots);
    }

    [Fact]
    public async Task AStaleChunkIdFromADifferentCompositionIsStillDetected()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>
        {
            ["src/A.cs"] = MakeFile("Acme.A", "Widget", "DoA"),
            ["src/B.cs"] = MakeFile("Acme.B", "Gadget", "DoB"),
        });
        StubEmbeddingClient embedder = new();
        IndexStore store = new(_dir);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        IndexBuilder baseBuilder = new(source, pipeline, embedder, store, Options.Create(new CodeIndexOptions()));
        OverlayIndexBuilder overlayBuilder = new(baseBuilder, source, _dir, maxOverlays: 4, activationThreshold: 1);
        CodeIndexService service = new(overlayBuilder, source, embedder);

        IndexSnapshot mainState = await service.RefreshAsync(TestContext.Current.CancellationToken);
        int mainGeneration = mainState.Header.Generation;

        // A big enough change (>= threshold of 1) to trigger the overlay/divergence path, which
        // always bumps the composition generation on activation of a different state.
        source.Set("src/A.cs", MakeFile("Acme.A", "Widget", "DoAFeature"));
        await service.RefreshAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<StaleChunkIdException>(
            () => service.GetChunkAsync(mainGeneration, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompositionMasksBaseChunksForOverriddenPathsAndHidesDeletedOnes()
    {
        CodeChunk baseChunkA = new()
        {
            FilePath = "src/A.cs", StartLine = 1, EndLine = 1, Kind = ChunkKind.Method,
            Symbol = "A.M", Signature = "void M()",
        };
        CodeChunk baseChunkB = new()
        {
            FilePath = "src/B.cs", StartLine = 1, EndLine = 1, Kind = ChunkKind.Method,
            Symbol = "B.Old", Signature = "void Old()",
        };
        CodeChunk baseChunkC = new()
        {
            FilePath = "src/C.cs", StartLine = 1, EndLine = 1, Kind = ChunkKind.Method,
            Symbol = "C.M", Signature = "void M()",
        };

        IndexSnapshot @base = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion, Model = "stub", Dimensions = 2, ChunkCount = 3, BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [baseChunkA, baseChunkB, baseChunkC],
            Fingerprints =
            [
                new FileFingerprint("src/A.cs", 1, DateTime.UnixEpoch, "hashA"),
                new FileFingerprint("src/B.cs", 1, DateTime.UnixEpoch, "hashB"),
                new FileFingerprint("src/C.cs", 1, DateTime.UnixEpoch, "hashC"),
            ],
            Vectors = [1f, 0f, 0f, 1f, 1f, 1f],
        };

        // Overlay overrides B with new content and carries no chunks of its own for A/C -- B is
        // "deleted" relative to base in this overlay (simulating a file removed on the branch).
        CodeChunk overlayChunkB = baseChunkB with { Symbol = "B.New", Signature = "void New()" };
        IndexSnapshot overlayData = new()
        {
            Header = @base.Header with { ChunkCount = 1 },
            Chunks = [overlayChunkB],
            Fingerprints = [new FileFingerprint("src/B.cs", 2, DateTime.UnixEpoch, "hashBNew")],
            Vectors = [0f, 1f],
        };

        IndexSnapshot composed = OverlayComposer.Compose(@base, overlayData, deletedPaths: ["src/C.cs"], generation: 5);

        Assert.Equal(5, composed.Header.Generation);
        Assert.Equal(2, composed.Chunks.Count);
        Assert.Contains(composed.Chunks, c => c.Symbol == "A.M");
        Assert.Contains(composed.Chunks, c => c.Symbol == "B.New");
        Assert.DoesNotContain(composed.Chunks, c => c.Symbol == "B.Old"); // masked by the overlay
        Assert.DoesNotContain(composed.Chunks, c => c.Symbol == "C.M");   // hidden: deleted in the overlay
        Assert.Equal(2, composed.Fingerprints.Count);
    }
}
