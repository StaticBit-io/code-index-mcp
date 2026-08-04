namespace CodeIndex.Core.Search;

/// <summary>
/// Caps how many of a single file's chunks may occupy the final result set, so a small <c>limit</c>
/// is not entirely filled by two or three sibling members of the one or two most central files while
/// a genuinely relevant — but slightly lower-ranked — file never gets a slot at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this targets.</b> <see cref="HybridRanker"/> fuses purely by rank position and
/// <see cref="CodeIndexService.SearchWithStatusAsync"/> then takes a flat <c>Take(limit)</c> off the
/// front of that fused order. Neither step has any notion of "file" — so when a topic has several
/// sibling declarations in the same file that all score highly on the vector branch alone, they can
/// consume most or all of a small <c>limit</c> before a different file's single, equally relevant
/// chunk is ever reached, even though that chunk ranked only a few places lower. Measured against the
/// real <c>wallet</c> project's index: a natural-language query about the network-unavailable UI flow
/// put three separate members of the same <c>NetworkUnavailableModal.razor.cs</c> class into 3 of 5
/// result slots at <c>limit=5</c>, leaving no room for <c>XrplSharpClientService.ExecuteIfConnected</c>
/// — a distinct, alternate failure path (the connectivity check that throws
/// <c>NotConnectedException</c>) that ranked 7th overall but 1st among files not already represented.
/// </para>
/// <para>
/// <b>Cap-then-backfill, not a hard exclusion.</b> This walks the already-fused, already-ordered
/// ranking once, keeping every non-exempt candidate (see <c>exemptIndices</c> on
/// <see cref="Diversify"/>) whose file has not yet reached <c>maxPerFile</c> such selections, and
/// setting aside (not dropping) every one that would exceed it. Once the capped pass is exhausted,
/// the set-aside candidates are appended back in their original rank order until <c>limit</c> is
/// reached. This guarantees the method never returns fewer results than a plain <c>Take(limit)</c>
/// would have.
/// </para>
/// <para>
/// <b>Why symbol-branch hits are exempt from the cap.</b> The first version of this fix capped every
/// candidate uniformly and regressed two existing <c>SearchQualityTests</c> golden queries: "TrustSet"
/// and "LedgerEntry" each legitimately match several sibling declarations in one file (a class via
/// <see cref="SymbolMatcher"/>'s exact/prefix band, plus that class's own const fields/properties via
/// its substring band) — exactly the "genuinely several members of one class" case a per-file cap is
/// expected to cost something on. But capping bumped the query's own named class out in favour of a
/// same-branch-depth but otherwise unrelated chunk from a completely different file, which only
/// happened to still be under its own file's cap — a strictly worse result, not a diversified one.
/// The distinguishing signal: <see cref="SymbolMatcher.Match"/> checks whether the caller's <em>whole</em>
/// query string is a literal substring of a chunk's symbol/signature/directory, so it essentially only
/// ever matches short, identifier-shaped queries like "TrustSet" — a natural-language sentence (the
/// shape of every query in the real reproduction above) never satisfies it, so the symbol branch
/// contributes nothing to those. Exempting symbol-branch hits from the cap therefore leaves precision
/// identifier lookups exactly as clustered as they earned the right to be, while still catching the
/// vector-only crowding the real defect is made of.
/// </para>
/// <para>
/// <b>Why capping (and not, say, per-directory capping or MMR) was chosen.</b> Per-directory capping
/// was considered — it would also have caught the network-unavailable case above (all three
/// crowding-out hits share both a file and a directory) — but it punishes exactly the "many small
/// files organised under one feature directory" shape this project's own <see cref="SymbolMatcher"/>
/// path-match band (see its remarks) already treats as legitimate breadth, not noise. An MMR-style
/// re-rank that penalises embedding similarity to already-selected results was also considered, but
/// it needs the embedding vectors themselves at re-rank time (not just each branch's already-reduced
/// rank position <see cref="HybridRanker"/> works with), which would mean threading raw vectors
/// through a layer that currently only ever sees <see cref="ScoredIndex"/>. A flat per-file cap needs
/// only two pieces of information — <see cref="Chunking.CodeChunk.FilePath"/> and which candidates the
/// symbol branch already vouched for — that <see cref="CodeIndexService"/> already has in hand for
/// every candidate.
/// </para>
/// </remarks>
public static class ResultDiversifier
{
    /// <summary>
    /// The default cap: at most this many non-exempt (see <see cref="Diversify"/>'s
    /// <c>exemptIndices</c>) chunks from a single file are taken during the capped pass before that
    /// file's further chunks are deferred to the backfill pass. Chosen, not derived: 1 (never more
    /// than a single chunk per file) was rejected as too aggressive — a class and its one standout
    /// override are routinely both worth showing together, and capping at 1 would separate them
    /// purely because they share a file, even when nothing else outranks the second. 2 keeps that
    /// common "class + its most relevant member" pairing intact while still stopping a single file
    /// from claiming a majority of a <c>limit=5</c> result set the way the network-unavailable case
    /// above did at 3.
    /// </summary>
    public const int DefaultMaxPerFile = 2;

    /// <summary>
    /// Selects up to <paramref name="limit"/> entries from <paramref name="ranked"/> — which must
    /// already be in final rank order (best first) — preferring breadth across files up to
    /// <paramref name="maxPerFile"/> before falling back to <paramref name="ranked"/>'s own order to
    /// fill any remaining slots. Relative order is preserved within both the capped and the
    /// backfilled portions; only entries that would have exceeded the cap are ever moved later.
    /// </summary>
    /// <param name="ranked">Fused hits in descending rank order, deep enough to give this method
    /// room to work with — see <see cref="CodeIndexService"/>'s branch-depth remarks. Passing a list
    /// already truncated to <paramref name="limit"/> defeats the point: there would be nothing left
    /// to backfill from.</param>
    /// <param name="filePathOf">Resolves a candidate's <see cref="ScoredIndex.Index"/> to the file
    /// path used to group candidates. A delegate rather than requiring the caller to pre-join chunk
    /// data, so this stays usable directly against <see cref="Chunking.CodeChunk"/>, a snapshot
    /// lookup, or a test double with no ceremony either way.</param>
    /// <param name="limit">Maximum number of entries to return. Non-positive returns empty.</param>
    /// <param name="exemptIndices">Indices (see <see cref="ScoredIndex.Index"/>) that never count
    /// against — and are never deferred by — the per-file cap, regardless of how many of their file's
    /// chunks have already been selected. Intended for the symbol branch's own hits — see this
    /// class's remarks for why a literal identifier match must not be sacrificed to make room for an
    /// unrelated chunk elsewhere. <see langword="null"/> (the default) exempts nothing.</param>
    /// <param name="maxPerFile">See <see cref="DefaultMaxPerFile"/>. Must be positive; a
    /// non-positive value would mean every non-exempt candidate is deferred and the capped pass
    /// never selects any of them, silently degrading to backfill-only order for the non-exempt
    /// portion — callers that want "no diversification at all" should not call this method rather
    /// than pass a value meant to disable it.</param>
    public static IReadOnlyList<ScoredIndex> Diversify(
        IReadOnlyList<ScoredIndex> ranked,
        Func<int, string> filePathOf,
        int limit,
        IReadOnlySet<int>? exemptIndices = null,
        int maxPerFile = DefaultMaxPerFile)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(filePathOf);

        if (maxPerFile <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPerFile), maxPerFile, "Must be positive.");
        }

        if (limit <= 0 || ranked.Count == 0)
        {
            return [];
        }

        if (ranked.Count <= limit)
        {
            // Nothing to diversify away from: every candidate is already being returned, so a
            // capped pass followed by backfill would just reconstruct the same list at extra cost.
            return ranked;
        }

        // Selection is recorded as flags over `ranked`'s own positions rather than by appending
        // to an output list, so the result can be emitted in the input's order at the end. This
        // matters because backfill runs after the capped pass: appending a deferred candidate
        // directly would place a rank-3 hit *behind* the rank-4 hit that displaced it, and the
        // tool's output would stop being ordered by relevance even though its `score` field still
        // said otherwise. Diversification is meant to change *which* hits come back, never the
        // order they are presented in.
        bool[] isSelected = new bool[ranked.Count];
        int selectedCount = 0;
        List<int>? deferredOrdinals = null;
        Dictionary<string, int> perFileCount = new(StringComparer.Ordinal);

        for (int ordinal = 0; ordinal < ranked.Count; ordinal++)
        {
            if (selectedCount == limit)
            {
                break;
            }

            ScoredIndex candidate = ranked[ordinal];

            if (exemptIndices is not null && exemptIndices.Contains(candidate.Index))
            {
                // A literal identifier match earned its place regardless of how many of its
                // file's chunks are already selected — and does not itself consume any of the
                // file's non-exempt quota, so it can never be the reason a later, genuinely
                // vector-only sibling gets deferred.
                isSelected[ordinal] = true;
                selectedCount++;
                continue;
            }

            string filePath = filePathOf(candidate.Index);
            int countSoFar = perFileCount.GetValueOrDefault(filePath);

            if (countSoFar < maxPerFile)
            {
                isSelected[ordinal] = true;
                selectedCount++;
                perFileCount[filePath] = countSoFar + 1;
            }
            else
            {
                (deferredOrdinals ??= []).Add(ordinal);
            }
        }

        // Backfill in rank order, so when the cap leaves slots unfilled the strongest deferred
        // candidates are the ones that come back — never an arbitrary subset.
        if (selectedCount < limit && deferredOrdinals is not null)
        {
            foreach (int ordinal in deferredOrdinals)
            {
                if (selectedCount == limit)
                {
                    break;
                }

                isSelected[ordinal] = true;
                selectedCount++;
            }
        }

        List<ScoredIndex> selected = new(selectedCount);
        for (int ordinal = 0; ordinal < ranked.Count; ordinal++)
        {
            if (isSelected[ordinal])
            {
                selected.Add(ranked[ordinal]);
            }
        }

        return selected;
    }
}
