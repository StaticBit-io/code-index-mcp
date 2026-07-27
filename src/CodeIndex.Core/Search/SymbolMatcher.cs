using CodeIndex.Core.Chunking;

namespace CodeIndex.Core.Search;

/// <summary>
/// Literal identifier matching over chunk symbols and signatures. Embeddings handle intent
/// well but treat an exact type or method name as just another token, so a query like
/// "where is TrustSetFlags" needs this branch — not the vector branch — to land reliably.
/// </summary>
public sealed class SymbolMatcher
{
    /// <summary>The query equals the leaf name (the part of <see cref="CodeChunk.Symbol"/>
    /// after the last '.') exactly.</summary>
    private const float ExactLeafScore = 1.0f;

    /// <summary>The leaf name starts with the query.</summary>
    private const float PrefixScore = 0.7f;

    /// <summary>The full, dotted symbol contains the query somewhere.</summary>
    private const float SubstringScore = 0.4f;

    /// <summary>The query only shows up in the signature (e.g. a parameter or return type),
    /// not in the symbol itself.</summary>
    private const float SignatureScore = 0.2f;

    private readonly IReadOnlyList<CodeChunk> _chunks;

    public SymbolMatcher(IReadOnlyList<CodeChunk> chunks)
    {
        _chunks = chunks;
    }

    /// <summary>
    /// Scores every chunk against <paramref name="query"/> and returns the
    /// <paramref name="topK"/> highest-scoring chunks, descending, ties broken by ascending
    /// index for deterministic output. An empty or whitespace-only query never matches
    /// anything, since it is not an identifier-like token worth searching for.
    /// </summary>
    public IReadOnlyList<ScoredIndex> Match(string query, int topK)
    {
        string term = query.Trim();
        if (term.Length == 0)
            return [];

        List<ScoredIndex> hits = [];

        for (int i = 0; i < _chunks.Count; i++)
        {
            float score = ScoreOne(_chunks[i], term);
            if (score > 0f)
                hits.Add(new ScoredIndex(i, score));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Index)
            .Take(topK)
            .ToArray();
    }

    private static float ScoreOne(CodeChunk chunk, string term)
    {
        string symbol = chunk.Symbol;
        int lastDot = symbol.LastIndexOf('.');
        string leaf = lastDot >= 0 ? symbol[(lastDot + 1)..] : symbol;

        if (leaf.Equals(term, StringComparison.OrdinalIgnoreCase))
            return ExactLeafScore;

        if (leaf.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return PrefixScore;

        if (symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
            return SubstringScore;

        if (chunk.Signature.Contains(term, StringComparison.OrdinalIgnoreCase))
            return SignatureScore;

        return 0f;
    }
}
