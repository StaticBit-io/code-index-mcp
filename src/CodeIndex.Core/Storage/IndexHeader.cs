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
