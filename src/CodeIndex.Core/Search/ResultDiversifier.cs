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
/// sibling declarations in the same file (a class and two of its methods, say) that all score highly,
/// they can consume most or all of a small <c>limit</c> before a different file's single, equally
/// relevant chunk is ever reached, even though that chunk ranked only a few places lower. Measured
/// against the real <c>wallet</c> project's index: a query about the network-unavailable UI flow
/// put three separate members of the same <c>NetworkUnavailableModal.razor.cs</c> class into 3 of 5
/// result slots at <c>limit=5</c>, leaving no room for <c>XrplSharpClientService.ExecuteIfConnected</c>
/// — a distinct, alternate failure path (the connectivity check that throws
/// <c>NotConnectedException</c>) that ranked 7th overall but 1st among files not already represented.
/// </para>
/// <para>
/// <b>Cap-then-backfill, not a hard exclusion.</b> This walks the already-fused, already-ordered
/// ranking once, keeping every candidate whose file has not yet reached <c>maxPerFile</c> selections
/// and setting aside (not dropping) every candidate that would exceed it. Once the capped pass is
/// exhausted, the set-aside candidates are appended back in their original rank order until <c>limit</c>
/// is reached. This guarantees the method never returns fewer results than a plain <c>Take(limit)</c>
/// would have — a query whose only real answer genuinely is five members of one class still gets all
/// five, just after every distinct file at or above their rank has already claimed its slots first.
/// The cost is that such a query's slice from that one dominant file arrives with lower-ranked
/// material from other files interleaved in front of some of its own further members, if there were
/// not enough distinct files above it to fill <c>limit</c> outright without dipping into the deferred
/// list — an ordering change, never a drop.
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
/// only the one piece of information — <see cref="Chunking.CodeChunk.FilePath"/> — that
/// <see cref="CodeIndexService"/> already has in hand for every candidate.
/// </para>
/// </remarks>
public static class ResultDiversifier
{
    /// <summary>
    /// The default cap: at most this many chunks from a single file are taken during the capped
    /// pass before that file's further chunks are deferred to the backfill pass. Chosen, not
    /// derived: 1 (never more than a single chunk per file) was rejected as too aggressive — a
    /// class and its one standout override are routinely both worth showing together, and capping
    /// at 1 would separate them purely because they share a file, even when nothing else outranks
    /// the second. 2 keeps that common "class + its most relevant member" pairing intact while
    /// still stopping a single file from claiming a majority of a <c>limit=5</c> result set the
    /// way the network-unavailable case above did at 3.
    /// </summary>
    public const int DefaultMaxPerFile = 2;

    /// <summary>
    /// Selects up to <paramref name="limit"/> entries from <paramref name="ranked"/> — which must
    /// already be in final rank order (best first) — preferring breadth across files up to
    /// <paramref name="maxPerFile"/> before falling back to <paramref name="ranked"/>'s own order
    /// to fill any remaining slots. Relative order is preserved within both the capped and the
    /// backfilled portions; only entries that would have exceeded the cap are ever moved later.
    /// </summary>
    /// <param name="ranked">Fused hits in descending rank order, deep enough to give this method
    /// room to work with — see <see cref="CodeIndexService"/>'s branch-depth remarks. Passing a
    /// list already truncated to <paramref name="limit"/> defeats the point: there would be
    /// nothing left to backfill from.</param>
    /// <param name="filePathOf">Resolves a candidate's <see cref="ScoredIndex.Index"/> to the file
    /// path used to group candidates. A delegate rather than requiring the caller to pre-join chunk
    /// data, so this stays usable directly against <see cref="Chunking.CodeChunk"/>, a snapshot
    /// lookup, or a test double with no ceremony either way.</param>
    /// <param name="limit">Maximum number of entries to return. Non-positive returns empty.</param>
    /// <param name="maxPerFile">See <see cref="DefaultMaxPerFile"/>. Must be positive; a
    /// non-positive value would mean every candidate is deferred and the capped pass never
    /// selects anything, silently degrading to backfill-only order — callers that want "no
    /// diversification at all" should not call this method rather than pass a value meant to
    /// disable it.</param>
    public static IReadOnlyList<ScoredIndex> Diversify(
        IReadOnlyList<ScoredIndex> ranked,
        Func<int, string> filePathOf,
        int limit,
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

        List<ScoredIndex> selected = new(Math.Min(limit, ranked.Count));
        List<ScoredIndex>? deferred = null;
        Dictionary<string, int> perFileCount = new(StringComparer.Ordinal);

        foreach (ScoredIndex candidate in ranked)
        {
            if (selected.Count == limit)
            {
                break;
            }

            string filePath = filePathOf(candidate.Index);
            int countSoFar = perFileCount.GetValueOrDefault(filePath);

            if (countSoFar < maxPerFile)
            {
                selected.Add(candidate);
                perFileCount[filePath] = countSoFar + 1;
            }
            else
            {
                (deferred ??= []).Add(candidate);
            }
        }

        if (selected.Count < limit && deferred is not null)
        {
            foreach (ScoredIndex candidate in deferred)
            {
                if (selected.Count == limit)
                {
                    break;
                }

                selected.Add(candidate);
            }
        }

        return selected;
    }
}
