namespace CodeIndex.Core.Storage;

/// <summary>
/// Fixed-size metadata describing an on-disk index: which model and dimensionality produced
/// the vectors, and how many chunks they cover. Persisted once per index so that a stale or
/// incompatible cache can be detected before it is ever used to answer a query.
/// </summary>
public sealed record IndexHeader
{
    /// <summary>
    /// Bumped whenever the on-disk layout changes in a way older readers cannot interpret.
    /// Any mismatch forces a full rebuild rather than an attempt to read a format that has
    /// since evolved. Currently 2: version 1 did not carry <c>ManifestDocument.VectorsHash</c>,
    /// so a version-1 manifest is correctly rejected rather than read as if it had one.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Identifies a manifest as belonging to this store's format, distinct from schema
    /// versioning: this catches "wrong file entirely" (e.g. someone points the store at an
    /// unrelated JSON file) before the schema/model/dimensions checks even run.
    /// </summary>
    public const string MagicSignature = "CIDXMAN1";

    public string Magic { get; init; } = MagicSignature;
    public required int SchemaVersion { get; init; }
    public required string Model { get; init; }
    public required int Dimensions { get; init; }
    public required int ChunkCount { get; init; }
    public required DateTime BuiltAtUtc { get; init; }

    /// <summary>
    /// Monotonically increasing counter identifying which "shape" of the chunk list a
    /// <see cref="Search.ProjectChunkId"/>'s ordinal was captured against. Bumped by <see
    /// cref="Indexing.IndexBuilder"/> only when a build/refresh actually adds, removes, or
    /// otherwise reorders chunks — exactly the operations that shift every subsequent chunk's
    /// ordinal position (see the ordinal-id volatility warning on <see cref="IndexSnapshot"/>). A
    /// refresh that touches files but leaves the chunk list the same shape (a content-only edit
    /// with no member added/removed, or the "git checkout" timestamp-only case) does <b>not</b>
    /// bump this, precisely so a caller's outstanding id is not invalidated by a change that could
    /// not possibly have moved it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not <c>required</c>, and defaults to 0.</b> This field was added after this project
    /// already had real, multi-minute-to-build indexes on disk. Making it <c>required</c> would
    /// mean every pre-existing manifest fails to deserialise (missing JSON property) and gets
    /// silently treated as corrupted by <see cref="Storage.IndexStore.LoadAsync"/> — which
    /// currently reacts to a corrupted load by deleting the cache and rebuilding from scratch (see
    /// <see cref="Indexing.IndexBuilder.RefreshAsync"/>). That would force an unannounced
    /// multi-minute rebuild the next time anyone refreshes an existing index, purely as a side
    /// effect of upgrading this server — not something a version bump should do silently. Bumping
    /// <see cref="CurrentSchemaVersion"/> instead would have the identical effect (it is exactly
    /// what that field is for), so it was rejected for the same reason. Defaulting to 0 costs
    /// nothing: a pre-upgrade cache simply starts counting generations from 0 the next time its
    /// shape actually changes, and since an MCP client starts a fresh server process per session,
    /// no caller can already be holding an id from a "previous" generation of a cache that has
    /// never been through this code before.
    /// </para>
    /// </remarks>
    public int Generation { get; init; }

    /// <summary>
    /// True only when this header was produced by the exact same schema version, embedding
    /// model, and dimensionality as requested. Vectors from a different model are not
    /// comparable to a query embedded with the current model — the cosine distances would
    /// still compute and still return a ranked list, just a meaningless one — so any mismatch
    /// here must force a full reindex rather than a degraded search.
    /// </summary>
    public bool IsCompatibleWith(string model, int dimensions) =>
        SchemaVersion == CurrentSchemaVersion &&
        Dimensions == dimensions &&
        string.Equals(Model, model, StringComparison.Ordinal);
}

/// <summary>
/// Thrown when the persisted index cannot be trusted: the manifest and vector file disagree
/// (including a content-hash mismatch — the pair was written by two different save
/// generations), one half of the pair is missing while the other exists, the manifest's magic
/// signature or chunk count doesn't match its own declared shape, or the manifest could not be
/// parsed at all. Callers should treat this the same as "no usable index" and trigger a full
/// rebuild — the alternative, silently loading a partially-written or inconsistent index,
/// would corrupt search results without any visible symptom.
/// </summary>
public sealed class IndexCorruptedException : Exception
{
    public IndexCorruptedException(string message) : base(message)
    {
    }

    public IndexCorruptedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
