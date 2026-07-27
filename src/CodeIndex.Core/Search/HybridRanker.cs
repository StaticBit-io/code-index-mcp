namespace CodeIndex.Core.Search;

/// <summary>
/// Fuses the vector-search branch and the symbol-search branch with Reciprocal Rank Fusion
/// (RRF). Chosen over weighted score blending because cosine similarity and literal-match
/// scores live on incomparable scales — fusing by rank position sidesteps tuning weights that
/// would drift with every embedding model change.
/// </summary>
public static class HybridRanker
{
    /// <summary>
    /// The standard RRF smoothing constant: it flattens the contribution of very high ranks
    /// so a single first-place finish does not automatically outrank an item that places
    /// respectably in both branches.
    /// </summary>
    private const double RankConstant = 60.0;

    /// <summary>
    /// Combines both ranked branches into one list, descending by fused score, ties broken by
    /// ascending index for deterministic output. A chunk found by both branches accumulates a
    /// contribution from each, so it can out-rank a chunk that is merely first in one branch —
    /// see <see cref="ScoredIndex"/> usages in the accompanying tests for a worked example.
    /// </summary>
    /// <remarks>
    /// That "found by both beats first-in-one" property holds unconditionally only while each
    /// input list is shorter than <c>RankConstant + 2</c> = 62 entries: a chunk placing
    /// last in two 62-long lists contributes exactly <c>2 / (60 + 62)</c>, which is precisely
    /// equal to a single first-place contribution of <c>1 / (60 + 1)</c>, and longer lists can
    /// tip the balance the other way. This is not a practical concern here — <see cref="VectorSearcher"/>
    /// and <see cref="SymbolMatcher"/> are both called with a page-sized <c>topK</c> (in the
    /// 20-50 range), well under that boundary — but it means the property is a consequence of
    /// how this is used, not a mathematical guarantee of RRF itself.
    /// </remarks>
    public static IReadOnlyList<ScoredIndex> Fuse(
        IReadOnlyList<ScoredIndex> vectorHits,
        IReadOnlyList<ScoredIndex> symbolHits,
        int topK)
    {
        Dictionary<int, double> fused = [];

        Accumulate(fused, vectorHits);
        Accumulate(fused, symbolHits);

        return fused
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Take(topK)
            .Select(entry => new ScoredIndex(entry.Key, (float)entry.Value))
            .ToArray();
    }

    private static void Accumulate(Dictionary<int, double> fused, IReadOnlyList<ScoredIndex> hits)
    {
        for (int rank = 0; rank < hits.Count; rank++)
        {
            double contribution = 1.0 / (RankConstant + rank + 1);
            fused[hits[rank].Index] = fused.GetValueOrDefault(hits[rank].Index) + contribution;
        }
    }
}
