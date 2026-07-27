using System.Text.Json.Nodes;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Storage;
using Xunit;

namespace CodeIndex.Core.Tests.Storage;

public sealed class IndexStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IndexSnapshot BuildSnapshot(int dimensions = 4)
    {
        CodeChunk chunk = new()
        {
            FilePath = "src/A.cs",
            StartLine = 1,
            EndLine = 10,
            Kind = ChunkKind.Method,
            Symbol = "A.B.C",
            Signature = "void C()",
            EmbedText = "irrelevant",
        };

        return new IndexSnapshot
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = dimensions,
                ChunkCount = 2,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [chunk, chunk with { Symbol = "A.B.D" }],
            Fingerprints = [new FileFingerprint("src/A.cs", 42, DateTime.UnixEpoch, "hash")],
            Vectors = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f],
        };
    }

    private static async Task<JsonObject> ReadManifestAsJsonAsync(IndexStore store, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(store.ManifestPath, cancellationToken);
        return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("expected manifest JSON");
    }

    private static async Task WriteManifestAsync(IndexStore store, JsonObject manifest, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(store.ManifestPath, manifest.ToJsonString(), cancellationToken);

    [Fact]
    public async Task SaveThenLoad_RoundTripsVectorsByteForByte()
    {
        IndexStore store = new(_dir);
        IndexSnapshot original = BuildSnapshot();

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        IndexSnapshot loaded = await store.LoadAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("expected a snapshot");

        Assert.Equal(original.Vectors, loaded.Vectors);
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("A.B.D", loaded.Chunks[1].Symbol);
        Assert.Equal("src/A.cs", loaded.Fingerprints[0].RelativePath);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenNothingWasSaved()
    {
        IndexStore store = new(_dir);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenVectorFileLengthDisagreesWithHeader()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        string vectorPath = Path.Combine(_dir, "vectors.bin");
        byte[] truncated = (await File.ReadAllBytesAsync(vectorPath, TestContext.Current.CancellationToken))[..16];
        await File.WriteAllBytesAsync(vectorPath, truncated, TestContext.Current.CancellationToken);

        IndexCorruptedException error = await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("vectors.bin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompatibleWith_RejectsDifferentModelOrDimensions()
    {
        IndexHeader header = BuildSnapshot().Header;

        Assert.True(header.IsCompatibleWith("qwen3-embedding:4b", 4));
        Assert.False(header.IsCompatibleWith("nomic-embed-text", 4));
        Assert.False(header.IsCompatibleWith("qwen3-embedding:4b", 1024));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenHeaderChunkCountDisagreesWithStoredChunks()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Header"]!["ChunkCount"] = 3;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        IndexCorruptedException error = await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ChunkCount", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenManifestExistsButVectorsFileIsMissing()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        File.Delete(store.VectorsPath);

        await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenVectorsFileExistsButManifestIsMissing()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        File.Delete(store.ManifestPath);

        await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsIndexCorruptedExceptionWhenManifestIsMalformed()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(store.ManifestPath, "{ this is not valid json", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesBothFilesAndIsSafeWhenTheyDoNotExist()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        store.Delete();

        Assert.False(File.Exists(store.ManifestPath));
        Assert.False(File.Exists(store.VectorsPath));

        // Must not throw when the files are already gone.
        store.Delete();
    }

    [Fact]
    public void Delete_IsSafeWhenTheCacheDirectoryNeverExisted()
    {
        // The realistic trigger: a forced reindex requested before the first ever build, so
        // nothing under _dir — not even the directory itself — has been created yet.
        IndexStore store = new(_dir);

        store.Delete();

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Delete_RemovesLeftoverTempFilesFromAnInterruptedSave()
    {
        Directory.CreateDirectory(_dir);
        IndexStore store = new(_dir);
        File.WriteAllBytes(store.VectorsPath + ".tmp", [1, 2, 3]);
        File.WriteAllText(store.ManifestPath + ".tmp", "{}");

        store.Delete();

        Assert.False(File.Exists(store.VectorsPath + ".tmp"));
        Assert.False(File.Exists(store.ManifestPath + ".tmp"));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEmptyIndex()
    {
        IndexStore store = new(_dir);
        IndexSnapshot empty = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = 4,
                ChunkCount = 0,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [],
            Fingerprints = [],
            Vectors = [],
        };

        await store.SaveAsync(empty, TestContext.Current.CancellationToken);
        IndexSnapshot? loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Chunks);
        Assert.Empty(loaded.Fingerprints);
        Assert.Empty(loaded.Vectors);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesEveryCodeChunkAndFingerprintField()
    {
        CodeChunk chunk = new()
        {
            FilePath = "src/Deep/Nested/Widget.cs",
            StartLine = 12,
            EndLine = 48,
            Kind = ChunkKind.Constructor,
            Symbol = "Acme.Widgets.Widget..ctor",
            Signature = "public Widget(int id, string name)",
            DocComment = "/// <summary>\n/// Creates a widget.\n/// </summary>",
            EmbedText = "public Widget(int id, string name)\n{\n    Id = id;\n    Name = name;\n}",
        };
        FileFingerprint fingerprint = new("src/Deep/Nested/Widget.cs", 999, DateTime.UnixEpoch, "deadbeef");

        IndexSnapshot snapshot = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = 4,
                ChunkCount = 1,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [chunk],
            Fingerprints = [fingerprint],
            Vectors = [0.1f, 0.2f, 0.3f, 0.4f],
        };

        IndexStore store = new(_dir);
        await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        IndexSnapshot loaded = await store.LoadAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("expected a snapshot");

        // EmbedText is deliberately NOT part of the persisted manifest ([JsonIgnore] on
        // CodeChunk.EmbedText): it only exists to feed the embedding model at index time, is
        // never read back for search or code display, and at project scale it measured out to
        // the majority of the manifest's bytes. A round-tripped chunk always comes back with
        // EmbedText empty, never the original text — that is the expected, intended behaviour,
        // not a bug, so the comparison below excludes it explicitly rather than by accident.
        CodeChunk expectedChunk = chunk with { EmbedText = string.Empty };

        // Record equality compares every remaining property, so a serialisation gap that
        // silently drops a field (e.g. DocComment, or the multi-line Signature) fails this.
        Assert.Equal(expectedChunk, loaded.Chunks[0]);
        Assert.Equal(fingerprint, loaded.Fingerprints[0]);
    }

    [Fact]
    public async Task SaveAsync_SerialisesChunkKindAsAStringNotTheUnderlyingNumber()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);

        // Guards against enum renumbering silently reinterpreting every stored index: if this
        // were serialised as the underlying int (Method = 6), inserting a new ChunkKind value
        // in the middle of the enum would change what every previously stored chunk decodes to.
        // Checked via the parsed JSON token kind (not a raw substring match) so the assertion
        // does not depend on whether the manifest happens to be indented.
        JsonNode kindNode = manifest["Chunks"]![0]!["Kind"]!;
        Assert.Equal(System.Text.Json.JsonValueKind.String, kindNode.GetValueKind());
        Assert.Equal("Method", kindNode.GetValue<string>());
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryChunkKindValue()
    {
        CodeChunk[] chunks = Enum.GetValues<ChunkKind>()
            .Select(kind => new CodeChunk
            {
                FilePath = "src/A.cs",
                StartLine = 1,
                EndLine = 2,
                Kind = kind,
                Symbol = $"A.{kind}",
                Signature = "void M()",
                EmbedText = "irrelevant",
            })
            .ToArray();

        IndexSnapshot snapshot = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = 1,
                ChunkCount = chunks.Length,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = chunks,
            Fingerprints = [],
            Vectors = new float[chunks.Length],
        };

        IndexStore store = new(_dir);
        await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        IndexSnapshot loaded = await store.LoadAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("expected a snapshot");

        Assert.Equal(chunks.Select(c => c.Kind), loaded.Chunks.Select(c => c.Kind));
    }

    [Fact]
    public async Task SaveAsync_ThrowsWhenHeaderDimensionsIsNotPositive()
    {
        IndexSnapshot snapshot = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = 0,
                ChunkCount = 0,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [],
            Fingerprints = [],
            Vectors = [],
        };
        IndexStore store = new(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(snapshot, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_ThrowsWhenHeaderChunkCountDisagreesWithChunksList()
    {
        IndexSnapshot baseline = BuildSnapshot();
        IndexSnapshot mismatched = baseline with { Chunks = [baseline.Chunks[0]] }; // Header.ChunkCount is still 2
        IndexStore store = new(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(mismatched, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_ThrowsWhenVectorsLengthDisagreesWithChunkCountTimesDimensions()
    {
        IndexSnapshot mismatched = BuildSnapshot() with { Vectors = [0.1f, 0.2f] };
        IndexStore store = new(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(mismatched, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenHeaderDimensionsIsNotPositive()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Header"]!["Dimensions"] = 0;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenMagicSignatureDoesNotMatch()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Header"]!["Magic"] = "NOT-A-CODE-INDEX-FILE";
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenHeaderIsNull()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Header"] = null;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenHeaderModelIsNull()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Header"]!["Model"] = null;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenChunksListIsNull()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Chunks"] = null;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenFingerprintsListIsNull()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Fingerprints"] = null;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenAChunkInTheListIsNull()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        JsonObject manifest = await ReadManifestAsJsonAsync(store, TestContext.Current.CancellationToken);
        manifest["Chunks"]!.AsArray()[0] = null;
        await WriteManifestAsync(store, manifest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IndexCorruptedException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenManifestAndVectorsComeFromDifferentSaveGenerationsOfTheSameShape()
    {
        // Reproduces the window between the two File.Move calls in SaveAsync: same ChunkCount
        // and Dimensions across generations (an incremental update that edits a method body
        // without adding or removing members), so shape checks alone cannot catch this — only
        // the content hash can. Direction A: vectors.bin is generation 2, manifest.json is
        // still generation 1's.
        IndexStore store = new(_dir);
        IndexSnapshot generation1 = BuildSnapshot();
        await store.SaveAsync(generation1, TestContext.Current.CancellationToken);
        string oldManifestJson = await File.ReadAllTextAsync(store.ManifestPath, TestContext.Current.CancellationToken);

        IndexSnapshot generation2 = generation1 with { Vectors = [9f, 9f, 9f, 9f, 9f, 9f, 9f, 9f] };
        await store.SaveAsync(generation2, TestContext.Current.CancellationToken);

        // Simulate the crash: put the old manifest back next to the new vectors.bin.
        await File.WriteAllTextAsync(store.ManifestPath, oldManifestJson, TestContext.Current.CancellationToken);

        IndexCorruptedException error = await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenVectorsAreStaleRelativeToManifestOfTheSameShape()
    {
        // Same window, other direction: manifest.json is generation 2 (and its VectorsHash),
        // vectors.bin is still generation 1's.
        IndexStore store = new(_dir);
        IndexSnapshot generation1 = BuildSnapshot();
        await store.SaveAsync(generation1, TestContext.Current.CancellationToken);
        byte[] oldVectorBytes = await File.ReadAllBytesAsync(store.VectorsPath, TestContext.Current.CancellationToken);

        IndexSnapshot generation2 = generation1 with { Vectors = [9f, 9f, 9f, 9f, 9f, 9f, 9f, 9f] };
        await store.SaveAsync(generation2, TestContext.Current.CancellationToken);

        // Simulate the crash: put the old vectors back next to the new manifest.json.
        await File.WriteAllBytesAsync(store.VectorsPath, oldVectorBytes, TestContext.Current.CancellationToken);

        IndexCorruptedException error = await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeCacheSizeBytes_ReturnsZero_WhenCacheDirectoryNeverExisted()
    {
        IndexStore store = new(_dir);

        Assert.Equal(0, store.ComputeCacheSizeBytes());
    }

    [Fact]
    public async Task ComputeCacheSizeBytes_SumsManifestAndVectorsFileSizes()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        long expected = new FileInfo(store.ManifestPath).Length + new FileInfo(store.VectorsPath).Length;

        Assert.Equal(expected, store.ComputeCacheSizeBytes());
    }

    [Fact]
    public async Task ComputeCacheSizeBytes_IncludesLeftoverTempFilesFromAnInterruptedSave()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);
        long beforeTempFile = store.ComputeCacheSizeBytes();

        File.WriteAllBytes(store.VectorsPath + ".tmp", [1, 2, 3, 4, 5]);

        Assert.Equal(beforeTempFile + 5, store.ComputeCacheSizeBytes());
    }
}
