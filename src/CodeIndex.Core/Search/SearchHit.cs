using CodeIndex.Core.Chunking;

namespace CodeIndex.Core.Search;

/// <summary>
/// A chunk ordinal paired with a similarity or match score. This is the shared currency
/// between <see cref="VectorSearcher"/>, <see cref="SymbolMatcher"/>, and
/// <see cref="HybridRanker"/>: none of them need the full <see cref="CodeChunk"/> to rank
/// results, only its position in the index.
/// </summary>
public readonly record struct ScoredIndex(int Index, float Score);

/// <summary>
/// A ranked, user-facing search result: the chunk plus its final fused score.
/// </summary>
public sealed record SearchHit
{
    public required int ChunkId { get; init; }

    /// <summary>The <see cref="Storage.IndexHeader.Generation"/> of the snapshot <see
    /// cref="ChunkId"/> was resolved against — carried alongside the ordinal so a caller-facing id
    /// (see <see cref="ProjectChunkId"/>) can name both and a later lookup can detect one that has
    /// gone stale instead of silently resolving it against a different chunk.</summary>
    public required int Generation { get; init; }

    public required CodeChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>
    /// Body excerpt for display. Defaults to empty — the body is read from the source
    /// provider at query time and is never cached alongside the chunk metadata.
    /// </summary>
    public string Excerpt { get; init; } = string.Empty;

    /// <summary>
    /// True when the source file's current size/timestamp no longer match the fingerprint that was
    /// current when this chunk's line range was captured — meaning the file was very likely edited
    /// in the window between that refresh and this excerpt actually being read (see
    /// <see cref="CodeIndexService"/>'s <c>IsExcerptPossiblyStaleAsync</c>). <see cref="Excerpt"/>
    /// is still populated either way: a probably-correct excerpt with a caveat is more useful than
    /// none at all.
    /// </summary>
    public bool ExcerptMayBeStale { get; init; }
}
