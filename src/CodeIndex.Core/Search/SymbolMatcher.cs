using CodeIndex.Core.Chunking;

namespace CodeIndex.Core.Search;

/// <summary>
/// Literal identifier matching over chunk symbols, signatures, and file paths. Embeddings handle
/// intent well but treat an exact type or method name as just another token, so a query like
/// "where is TrustSetFlags" needs this branch — not the vector branch — to land reliably. The
/// file path is consulted last and weakest (see <see cref="PathScore"/>): a directory name is
/// often the strongest hint a developer actually has (e.g. "AddressCodec"), but it is also the
/// least specific of the three signals, since it says nothing about which declaration in that
/// directory is the right one — and unlike the symbol/signature bands, which score one specific
/// declaration, a directory match is shared by every file underneath it.
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

    /// <summary>
    /// The query only shows up in the chunk's containing <em>directory</em> — deliberately not
    /// the file name, see remarks — and not in the symbol or signature. Weakest band: a path
    /// match is the least specific of the three signals this matcher considers, and is scored
    /// accordingly — half of <see cref="SignatureScore"/>, mirroring a symbol:signature:path
    /// weighting of 3:2:1 that a comparable system arrived at independently (this matcher's own
    /// symbol-side bands already span 0.4-1.0, i.e. "3", and <see cref="SignatureScore"/> is "2",
    /// so "1" is 0.1).
    /// </summary>
    /// <remarks>
    /// <b>Directory only, not the file name — measured, not assumed.</b> Matching the file name
    /// too seems appealing (e.g. surfacing sibling response types declared alongside
    /// <c>AccountInfo</c> in <c>AccountInfo.cs</c>), and an earlier version of this band did
    /// include it. But a file name match shares a property with a leaf/prefix/substring match
    /// that a directory match does not: both single out one specific, already-well-matched
    /// entity (that one file, that one type) rather than a whole area of the codebase. When a
    /// chunk's own file name is a near-exact match for the query, every OTHER, textually
    /// unrelated chunk that merely happens to live in that same file rides along at
    /// <see cref="PathScore"/> — and <see cref="HybridRanker"/> fuses branches purely by rank
    /// position, not raw score (see its own remarks), so once such a chunk is included in the
    /// branch at all it can accumulate a "found in both branches" bonus competitive with the
    /// genuine hit. This is not hypothetical: including the file name regressed an existing
    /// golden query in <c>SearchQualityTests</c> — querying "TrustSet" (which prefix-matches the
    /// <c>TrustSetFlags</c> class, the intended hit) also file-name-matched the unrelated
    /// <c>TrustLine</c> class declared two lines below it in the same <c>TrustSetFlags.cs</c>,
    /// and <c>TrustLine</c>'s resulting fused rank edged out the real answer for a top-3 slot by
    /// a fused-score margin of about 0.0001. A directory, by contrast, almost always groups many
    /// files for an organisational reason (a namespace, a feature area), so a query naming one is
    /// asking "what lives in this area," not "what is this one specific thing" — and unlike a
    /// file name, it only rarely coincides with the identity of a single strong match the way a
    /// class's own file name routinely does. Measured against the real xrplcsharp index, a
    /// directory match still finds real value the symbol/signature bands miss entirely — e.g.
    /// test helper and generated-resource files under <c>Xrpl.AddressCodec.Test/</c> that share
    /// no token with "AddressCodec" itself (see <see cref="MaxPathOnlyMatches"/> for the flooding
    /// side of that same measurement). What is given up by excluding the file name is smaller and
    /// safer: a file that already contains a strong symbol/signature match — exactly the
    /// situation where the risk above applies — does not need a weak path match to also be found.
    /// </remarks>
    private const float PathScore = 0.1f;

    /// <summary>
    /// Caps how many chunks may score via <see cref="PathScore"/> alone in a single
    /// <see cref="Match"/> call. Unlike the symbol bands above — where many tied chunks sharing
    /// one score are usually all genuinely the same match (e.g. a partial class split across
    /// files) — a path match only means "lives somewhere under a directory whose name contains
    /// the query," which a whole, unrelated directory of files can satisfy at once. Measured
    /// against the real 8,897-chunk xrplcsharp index: querying "Transactions" (the name of a
    /// 1,825-chunk directory) picks up 466 additional chunks whose path matches but whose symbol
    /// and signature do not — all tied at <see cref="PathScore"/>, with nothing to distinguish
    /// them. Left uncapped, that flood alone would fill every one of the branch's top-50 slots
    /// (see <c>CodeIndexService.MinBranchDepth</c>) with equally-arbitrary noise whenever a
    /// query happens to name a large, generically-organised directory. The cap is applied in
    /// ascending chunk-index order (see <see cref="Match"/>), the same deterministic tie-break
    /// already used for same-scoring hits elsewhere in this class.
    /// </summary>
    private const int MaxPathOnlyMatches = 20;

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
        int pathOnlyBudget = MaxPathOnlyMatches;

        for (int i = 0; i < _chunks.Count; i++)
        {
            float score = ScoreOne(_chunks[i], term);

            if (score == PathScore)
            {
                if (pathOnlyBudget <= 0)
                    continue;

                pathOnlyBudget--;
            }

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

        if (DirectoryOf(chunk.FilePath).Contains(term, StringComparison.OrdinalIgnoreCase))
            return PathScore;

        return 0f;
    }

    /// <summary>
    /// The directory portion of <paramref name="filePath"/> (a '/'-separated relative path — see
    /// <see cref="CodeChunk.FilePath"/>), i.e. everything before the last '/', excluding the file
    /// name itself. Empty for a file with no directory component. See <see cref="PathScore"/>'s
    /// remarks for why the file name is deliberately excluded here.
    /// </summary>
    private static string DirectoryOf(string filePath)
    {
        int lastSlash = filePath.LastIndexOf('/');
        return lastSlash >= 0 ? filePath[..lastSlash] : string.Empty;
    }
}
