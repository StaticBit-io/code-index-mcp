using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;

namespace CodeIndex.Core.Storage;

/// <summary>
/// Persists an <see cref="IndexSnapshot"/> as two files in one directory: a text
/// <c>manifest.json</c> (header, chunk metadata, file fingerprints) and a headerless
/// <c>vectors.bin</c> holding the flat <c>float32</c> vector array. The vector buffer is
/// always reinterpreted as a whole via <see cref="MemoryMarshal"/> — never walked
/// element-by-element — because at project scale (thousands of chunks x hundreds of
/// dimensions) a per-element loop is exactly the mistake this project was built to avoid.
/// </summary>
/// <remarks>
/// <see cref="IndexStore"/> is the one component outside <c>Sources/</c> allowed to touch
/// <see cref="File"/> / <see cref="Directory"/> directly: the cache it owns lives outside the
/// indexed project, so it is not source code subject to <c>ISourceProvider</c>.
/// </remarks>
public sealed class IndexStore
{
    private const string ManifestFileName = "manifest.json";
    private const string VectorsFileName = "vectors.bin";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Not indented: EmbedText is already excluded (see CodeChunk.EmbedText), and the
        // remaining content is still plain, greppable JSON without paying for whitespace on
        // every one of ~8700 chunk objects.
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public IndexStore(string directory)
    {
        _directory = directory;
    }

    public string ManifestPath => Path.Combine(_directory, ManifestFileName);
    public string VectorsPath => Path.Combine(_directory, VectorsFileName);

    /// <summary>
    /// Writes the manifest and vector file atomically (temp file + rename each) so a process
    /// killed mid-save can never leave a half-written file in place. This protects against a
    /// killed process, not a power cut: renames are not flushed to disk explicitly, so a
    /// hard power loss right after a rename could still leave metadata unsynced on some
    /// filesystems. That risk is accepted here — the cache is fully rebuildable from source,
    /// so paying for <c>fsync</c>/<c>FileOptions.WriteThrough</c> on every save is not worth it.
    /// </summary>
    /// <remarks>
    /// The vector file is committed (renamed into place) first and the manifest second. The
    /// manifest is what <see cref="LoadAsync"/> reads to know how many bytes the vector file is
    /// supposed to contain — and, via <see cref="ManifestDocument.VectorsHash"/>, exactly which
    /// generation of vectors it pairs with — so it acts as the commit record for the pair. A
    /// process killed between the two renames leaves either the fully-old pair, the fully-new
    /// pair, or an old manifest paired with new vectors; <see cref="LoadAsync"/> detects the
    /// last case unconditionally via the hash check below, not just when the shape happens to
    /// differ.
    /// </remarks>
    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateForSave(snapshot);

        Directory.CreateDirectory(_directory);

        string vectorsTempPath = VectorsPath + TempSuffix;
        string manifestTempPath = ManifestPath + TempSuffix;

        ulong vectorsHash = WriteVectors(vectorsTempPath, snapshot.Vectors);
        File.Move(vectorsTempPath, VectorsPath, overwrite: true);

        ManifestDocument document = new()
        {
            Header = snapshot.Header,
            Chunks = snapshot.Chunks,
            Fingerprints = snapshot.Fingerprints,
            VectorsHash = vectorsHash,
        };

        FileStream manifestStream = new(manifestTempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (manifestStream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(manifestStream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(manifestTempPath, ManifestPath, overwrite: true);
    }

    /// <summary>
    /// Loads the persisted snapshot, or <see langword="null"/> when no index has ever been
    /// saved (neither file exists). Any state where exactly one of the two files exists is
    /// treated as corruption rather than "no index": that state is only reachable via an
    /// interrupted write or manual tampering, both of which mean the cache cannot be trusted,
    /// and silently starting from an empty index would hide that rather than surfacing it.
    /// </summary>
    public async Task<IndexSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        bool manifestExists = File.Exists(ManifestPath);
        bool vectorsExists = File.Exists(VectorsPath);

        if (!manifestExists && !vectorsExists)
            return null;

        if (manifestExists != vectorsExists)
        {
            string missing = manifestExists ? VectorsFileName : ManifestFileName;
            throw new IndexCorruptedException(
                $"Index is incomplete: '{missing}' is missing while its counterpart exists. " +
                "This looks like an interrupted write; delete the cache directory and rebuild.");
        }

        ManifestDocument document = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        ValidateManifest(document);

        IndexHeader header = document.Header;

        if (!string.Equals(header.Magic, IndexHeader.MagicSignature, StringComparison.Ordinal))
        {
            throw new IndexCorruptedException(
                $"'{ManifestFileName}' does not have the expected magic signature; it is not a {nameof(CodeIndex)} index manifest.");
        }

        if (header.ChunkCount != document.Chunks.Count)
        {
            throw new IndexCorruptedException(
                $"'{ManifestFileName}' is inconsistent: header.ChunkCount is {header.ChunkCount} " +
                $"but {document.Chunks.Count} chunks are stored.");
        }

        if (header.Dimensions <= 0)
        {
            throw new IndexCorruptedException(
                $"'{ManifestFileName}' declares a non-positive Dimensions ({header.Dimensions}).");
        }

        long expectedByteLength = (long)header.ChunkCount * header.Dimensions * sizeof(float);

        (float[] vectors, ulong actualHash) = ReadVectors(expectedByteLength, header.ChunkCount * header.Dimensions);

        if (actualHash != document.VectorsHash)
        {
            throw new IndexCorruptedException(
                $"'{VectorsFileName}' content hash does not match the hash recorded in '{ManifestFileName}'. " +
                "The two files were written by different save generations (e.g. an interrupted save); " +
                "delete the cache directory and rebuild.");
        }

        return new IndexSnapshot
        {
            Header = header,
            Chunks = document.Chunks,
            Fingerprints = document.Fingerprints,
            Vectors = vectors,
        };
    }

    /// <summary>Removes the manifest, the vector file, and any leftover temp files from an
    /// interrupted save. Safe to call when some, all, or none of them exist — including when
    /// the containing directory itself does not exist.</summary>
    public void Delete()
    {
        DeleteIfExists(ManifestPath);
        DeleteIfExists(VectorsPath);
        DeleteIfExists(ManifestPath + TempSuffix);
        DeleteIfExists(VectorsPath + TempSuffix);
    }

    /// <summary>
    /// Total bytes on disk under the cache directory this store owns (manifest, vectors, and
    /// any leftover temp files from an interrupted save) — 0 if the directory does not exist.
    /// Exists so callers that only want to report the cache's on-disk footprint (e.g. the
    /// server's <c>--status</c> CLI path) go through <see cref="IndexStore"/>, the one
    /// sanctioned place outside <c>Sources/</c> allowed to touch <see cref="File"/>/
    /// <see cref="Directory"/> directly (see the class remarks), instead of duplicating that
    /// exemption at the call site.
    /// </summary>
    public long ComputeCacheSizeBytes()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Checked at the point a snapshot is handed to us to save, so a shape mistake
    /// (e.g. an embedding batch that came back short) fails loudly here rather than surfacing
    /// as an unexplained <see cref="IndexCorruptedException"/> on the next load.</summary>
    private static void ValidateForSave(IndexSnapshot snapshot)
    {
        if (snapshot.Header.Dimensions <= 0)
        {
            throw new ArgumentException(
                $"Header.Dimensions must be positive, was {snapshot.Header.Dimensions}.", nameof(snapshot));
        }

        if (snapshot.Header.ChunkCount != snapshot.Chunks.Count)
        {
            throw new ArgumentException(
                $"Header.ChunkCount ({snapshot.Header.ChunkCount}) does not match Chunks.Count ({snapshot.Chunks.Count}).",
                nameof(snapshot));
        }

        long expectedLength = (long)snapshot.Header.ChunkCount * snapshot.Header.Dimensions;
        if (snapshot.Vectors.LongLength != expectedLength)
        {
            throw new ArgumentException(
                $"Vectors.Length ({snapshot.Vectors.LongLength}) does not match " +
                $"Header.ChunkCount x Header.Dimensions ({expectedLength}).",
                nameof(snapshot));
        }
    }

    /// <summary>
    /// Writes the flat vector buffer as raw bytes via a single whole-buffer reinterpretation
    /// (<see cref="MemoryMarshal.AsBytes{T}(Span{T})"/>) and returns its <c>XxHash3</c>, which
    /// becomes <see cref="ManifestDocument.VectorsHash"/> — the token that binds this exact
    /// generation of vectors to the manifest that describes it. Synchronous by design: this is
    /// a single bulk copy into a local file, not a loop, so there is nothing for async I/O to
    /// overlap with — reaching for <c>RandomAccess</c>/async streams here would need an
    /// unsafe custom <c>MemoryManager&lt;byte&gt;</c> just to get a <c>Memory&lt;byte&gt;</c>
    /// view of a <c>float[]</c>, for no measurable benefit on a cache directory write.
    /// </summary>
    private static ulong WriteVectors(string path, float[] vectors)
    {
        using FileStream vectorsStream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        ReadOnlySpan<byte> vectorBytes = MemoryMarshal.AsBytes<float>(vectors.AsSpan());
        vectorsStream.Write(vectorBytes);
        return XxHash3.HashToUInt64(vectorBytes);
    }

    /// <summary>
    /// Reads <c>vectors.bin</c> straight into a single freshly-allocated <see cref="float"/>
    /// array — no intermediate <c>byte[]</c> — by reinterpreting that array's own backing
    /// memory as bytes and reading directly into it. The previous version read into a
    /// <c>byte[]</c> and then <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/>'d a copy
    /// out of it, holding both the full byte buffer and the full float buffer in the Large
    /// Object Heap at once (roughly double the target's ~35 MB).
    /// <see cref="Stream.ReadExactly(Span{byte})"/> throws if the file is shorter than expected
    /// instead of silently leaving the tail of an uninitialised array as garbage.
    /// </summary>
    private (float[] Vectors, ulong Hash) ReadVectors(long expectedByteLength, int floatCount)
    {
        using FileStream vectorsStream = new(VectorsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (vectorsStream.Length != expectedByteLength)
        {
            throw new IndexCorruptedException(
                $"'{VectorsFileName}' is {vectorsStream.Length} bytes but the manifest expects " +
                $"{expectedByteLength} bytes ({floatCount} floats x 4).");
        }

        float[] vectors = GC.AllocateUninitializedArray<float>(floatCount);
        Span<byte> destination = MemoryMarshal.AsBytes<float>(vectors.AsSpan());
        vectorsStream.ReadExactly(destination);

        return (vectors, XxHash3.HashToUInt64(destination));
    }

    private async Task<ManifestDocument> ReadManifestAsync(CancellationToken cancellationToken)
    {
        FileStream manifestStream = new(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (manifestStream.ConfigureAwait(false))
        {
            try
            {
                ManifestDocument? document = await JsonSerializer
                    .DeserializeAsync<ManifestDocument>(manifestStream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                return document ?? throw new IndexCorruptedException($"'{ManifestFileName}' deserialised to nothing.");
            }
            catch (JsonException ex)
            {
                throw new IndexCorruptedException($"'{ManifestFileName}' is malformed and could not be parsed.", ex);
            }
        }
    }

    /// <summary>
    /// Guards against a syntactically-valid manifest whose content is still unusable: `required`
    /// in System.Text.Json only checks that a JSON property was present, not that its value
    /// wasn't <c>null</c> — so <c>{"Header":null}</c>, <c>{"Chunks":[null]}</c> or
    /// <c>{"Header":{"Model":null}}</c> all deserialise successfully and hand back nulls in
    /// places the C# type system promises are non-null. Left unchecked, that null surfaces as an
    /// unexplained <see cref="NullReferenceException"/> far from here (or, worse, silently flows
    /// into a chunk with a null <c>FilePath</c>) instead of the clear, actionable
    /// <see cref="IndexCorruptedException"/> this throws.
    /// </summary>
    private static void ValidateManifest(ManifestDocument document)
    {
        if (document.Header is null)
            throw new IndexCorruptedException($"'{ManifestFileName}' has a null 'Header'.");

        if (document.Header.Model is null)
            throw new IndexCorruptedException($"'{ManifestFileName}' has a null 'Header.Model'.");

        if (document.Chunks is null)
            throw new IndexCorruptedException($"'{ManifestFileName}' has a null 'Chunks' list.");

        if (document.Fingerprints is null)
            throw new IndexCorruptedException($"'{ManifestFileName}' has a null 'Fingerprints' list.");

        for (int i = 0; i < document.Chunks.Count; i++)
        {
            CodeChunk chunk = document.Chunks[i];
            if (chunk is null)
                throw new IndexCorruptedException($"'{ManifestFileName}' has a null chunk at index {i}.");

            if (chunk.FilePath is null || chunk.Symbol is null || chunk.Signature is null || chunk.DocComment is null)
            {
                throw new IndexCorruptedException(
                    $"'{ManifestFileName}' has a chunk at index {i} with a null required text field.");
            }
        }

        for (int i = 0; i < document.Fingerprints.Count; i++)
        {
            FileFingerprint fingerprint = document.Fingerprints[i];
            if (fingerprint is null)
                throw new IndexCorruptedException($"'{ManifestFileName}' has a null fingerprint at index {i}.");

            if (fingerprint.RelativePath is null || fingerprint.ContentHash is null)
            {
                throw new IndexCorruptedException(
                    $"'{ManifestFileName}' has a fingerprint at index {i} with a null required text field.");
            }
        }
    }

    /// <summary>Everything the manifest holds except the vectors, which live in <c>vectors.bin</c>.</summary>
    private sealed record ManifestDocument
    {
        public required IndexHeader Header { get; init; }
        public required IReadOnlyList<CodeChunk> Chunks { get; init; }
        public required IReadOnlyList<FileFingerprint> Fingerprints { get; init; }

        /// <summary><c>XxHash3</c> of the exact <c>vectors.bin</c> bytes this manifest was
        /// saved alongside. Binds the pair together: two renames are not atomic as a unit, so
        /// this is what lets <see cref="LoadAsync"/> detect a manifest paired with vectors from
        /// a different save generation even when chunk count and dimensions happen to match.</summary>
        public required ulong VectorsHash { get; init; }
    }
}
