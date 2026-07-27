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
    public required CodeChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>
    /// Body excerpt for display. Defaults to empty — the body is read from the source
    /// provider at query time and is never cached alongside the chunk metadata.
    /// </summary>
    public string Excerpt { get; init; } = string.Empty;
}
