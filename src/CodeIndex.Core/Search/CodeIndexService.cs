using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;

namespace CodeIndex.Core.Search;

/// <summary>
/// Result of a hybrid search: the ranked hits, plus whether the vector branch degraded because
/// embeddings were unavailable and, if so, why.
/// </summary>
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, bool EmbeddingsUnavailable, string? Warning);

/// <summary>
/// The single entry point an MCP tool calls: refreshes the index, runs the hybrid (vector +
/// symbol) search, and reads excerpts on demand. Deliberately transport-agnostic — nothing here
/// knows it is being called from an MCP tool, a test, or anything else.
/// </summary>
/// <remarks>
/// <para>
/// Every public method that touches the index calls <see cref="RefreshAsync"/> (directly or via
/// <see cref="SearchWithStatusAsync"/>/<see cref="GetChunkAsync"/>, both of which go through <see
/// cref="RefreshOrFallBackAsync"/>) before doing anything else, so the index is never more than
/// one refresh pass out of date. The current snapshot is held in <see cref="Current"/> and only
/// ever replaced while holding <see cref="_gate"/>, and the cached snapshot is always passed back
/// into <see cref="IndexBuilder.RefreshAsync"/> so a no-change refresh costs a stat pass per file
/// rather than a full reload from disk. The gate also means two overlapping tool calls never both
/// rebuild/refresh the on-disk store at the same time.
/// </para>
/// <para>
/// <b>A stopped embedding backend degrades search; it must not disable it.</b> If a source file
/// changed since the last successful refresh, that refresh needs to re-embed the changed chunks —
/// which fails if the embedding backend is unreachable. <see cref="RefreshOrFallBackAsync"/>
/// catches exactly that failure and falls back to the last snapshot this instance successfully
/// produced, so symbol search keeps working against a (possibly one-file-stale) index instead of
/// the whole call failing. Only when no snapshot has ever been built does that failure propagate —
/// there is nothing left to degrade to. <see cref="RebuildAsync"/> deliberately has no such
/// fallback: an explicit, full rebuild with no working embedder is meaningless by definition, so
/// it is correct for it to throw.
/// </para>
/// <para>
/// <b>Design decision: filtering happens before either search branch runs, not after.</b>
/// <see cref="ChunkKind"/> and the path substring are applied first, in
/// <see cref="BuildCandidateIndices"/>, to produce a candidate set of chunk ordinals; <see
/// cref="VectorSearcher"/> and <see cref="SymbolMatcher"/> then only ever see that subset, so
/// their branch-depth cutoff (at least <see cref="MinBranchDepth"/>, more if the caller's
/// <c>limit</c> asks for more — see <see cref="SearchWithStatusAsync"/>) is spent entirely on
/// chunks that could actually be returned. The alternative — running both branches unfiltered and
/// only filtering their top-ranked output — would silently distort results whenever a filter is
/// narrow: a <c>pathFilter</c> matching only a handful of rare files could come back empty even
/// though matching chunks exist deeper in the unfiltered ranking, simply because none of them made
/// it into either branch's cutoff before filtering ever ran.
/// </para>
/// <para>
/// The cost of filtering first is that a filtered query pays for a fresh, candidate-sized copy of
/// the vector buffer on every call (see <see cref="RunVectorBranchAsync"/>) instead of reusing
/// the snapshot's own array directly. The failure mode this trades in: a broad filter that still
/// matches most of the corpus (e.g. a one-character <c>pathFilter</c>) copies nearly the whole
/// vector buffer on every single query, which is strictly more work than an unfiltered scan plus
/// a cheap post-hoc filter would have been. At this project's real scale (a few thousand chunks x
/// ~1k dimensions) that worst case is still on the order of a full unfiltered vector scan — i.e.
/// low milliseconds — so it was chosen over the alternative's correctness bug. The common,
/// unfiltered case (no <c>kind</c>, no <c>pathFilter</c>) pays nothing extra at all: <see
/// cref="BuildCandidateIndices"/> returns <c>null</c> in that case specifically so both branch
/// runners search the snapshot's own arrays with no copy.
/// </para>
/// </remarks>
public sealed class CodeIndexService
{
    /// <summary>The minimum number of hits each branch (vector, symbol) contributes before
    /// fusion, regardless of the caller's requested <c>limit</c>. A small requested limit must
    /// not starve fusion quality: a chunk ranked #40 in the vector branch but #2 in the symbol
    /// branch can only combine into a strong fused result if both branches were searched at
    /// least this deep. See <see cref="SearchWithStatusAsync"/> for how the effective depth for
    /// a given call is derived from this floor and the caller's <c>limit</c>.</summary>
    private const int MinBranchDepth = 50;

    /// <summary>Excerpts shown to callers are capped at this many lines.</summary>
    private const int MaxExcerptLines = 15;

    private readonly IndexBuilder _builder;
    private readonly ISourceProvider _source;
    private readonly IEmbeddingClient _embedder;
    private readonly float _minCosineSimilarity;

    /// <summary>Serialises every refresh/rebuild so concurrent tool calls never race each other
    /// into rebuilding the on-disk store at the same time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IndexSnapshot? _current;

    /// <param name="builder">Builds/refreshes the on-disk index this service searches.</param>
    /// <param name="source">Reads chunk excerpts/bodies on demand at query time.</param>
    /// <param name="embedder">Embeds search queries for the vector branch.</param>
    /// <param name="minCosineSimilarity">
    /// Relevance floor for the vector branch — see <see cref="EmbeddingOptions.MinCosineSimilarity"/>
    /// for how the project's real default (<see cref="EmbeddingOptions.DefaultMinCosineSimilarity"/>)
    /// was measured and what it is for. Defaults here to <see cref="double.NegativeInfinity"/> —
    /// "no floor," matching <see cref="VectorSearcher.Search"/>'s own default — deliberately not to
    /// the project's real default: most callers that construct this class directly (nearly every
    /// test in this codebase) are stand-ins that embed with no real semantic signal at all (see
    /// e.g. the stub embedding clients' own remarks), and a floor tuned for a real embedding
    /// model's score distribution is meaningless — worse, actively misleading — applied to those.
    /// <see cref="ProjectRegistry"/> is what wires the real, measured default through from
    /// configuration for the one path that actually talks to a real embedding backend.
    /// </param>
    public CodeIndexService(
        IndexBuilder builder,
        ISourceProvider source,
        IEmbeddingClient embedder,
        double minCosineSimilarity = double.NegativeInfinity)
    {
        _builder = builder;
        _source = source;
        _embedder = embedder;
        _minCosineSimilarity = (float)minCosineSimilarity;
    }

    /// <summary>The most recently loaded/built snapshot, or <c>null</c> before the first refresh.</summary>
    public IndexSnapshot? Current => _current;

    /// <summary>
    /// Brings the index up to date, passing the cached snapshot (if any) to <see
    /// cref="IndexBuilder.RefreshAsync"/> so a no-change refresh stays cheap. Safe to call
    /// concurrently: overlapping callers are serialised by <see cref="_gate"/>, and each one reads
    /// <see cref="_current"/> only after acquiring it, so every caller refreshes against the
    /// latest snapshot rather than a stale one captured before the lock.
    /// </summary>
    public async Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IndexSnapshot snapshot = await _builder.RefreshAsync(cancellationToken, _current).ConfigureAwait(false);
            _current = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces a full rebuild from scratch. Serialised by the same gate as <see cref="RefreshAsync"/>.</summary>
    public async Task<IndexSnapshot> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IndexSnapshot snapshot = await _builder.BuildAsync(cancellationToken).ConfigureAwait(false);
            _current = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Outcome of <see cref="RefreshOrFallBackAsync"/>: either a fresh snapshot (<see
    /// cref="IndexStale"/> false, <see cref="StaleWarning"/> null), or — when the refresh itself
    /// failed because embeddings were unavailable — the last known-good snapshot together with a
    /// warning explaining that recent edits are not reflected.
    /// </summary>
    private readonly record struct RefreshOutcome(IndexSnapshot Snapshot, bool IndexStale, string? StaleWarning);

    /// <summary>
    /// Attempts <see cref="RefreshAsync"/>; if it fails with <see
    /// cref="EmbeddingUnavailableException"/> — a source file changed since the last successful
    /// refresh and its chunks need re-embedding, but the embedding backend is unreachable — falls
    /// back to the last snapshot this instance successfully produced instead of propagating the
    /// exception. A stopped Ollama must not make symbol search unusable just because the index
    /// happens to be one file behind: the acceptance bar is "symbol search still works," and that
    /// requires *a* snapshot to search, not necessarily the newest one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fallback snapshot can come from disk, not just from this instance's own history.</b>
    /// An MCP client starts a fresh server process per session, so <see cref="_current"/> is
    /// <c>null</c> on the very first call regardless of whether a working index already sits on
    /// disk from a previous run. Without seeding, that first call's failure would have nothing to
    /// fall back to and would rethrow even though a perfectly usable snapshot exists — silently
    /// contradicting the premise that this method degrades instead of failing. <see
    /// cref="SeedFromStoreIfEmptyAsync"/> closes that gap: when <see cref="_current"/> is still
    /// <c>null</c>, it loads the on-disk snapshot (if any) into <see cref="_current"/> *before*
    /// attempting the real refresh below, so a refresh that fails immediately still has that
    /// loaded snapshot to fall back to. This costs nothing extra on the common path: <see
    /// cref="RefreshAsync"/> passes <see cref="_current"/> into <see
    /// cref="IndexBuilder.RefreshAsync"/> as its <c>current</c> parameter, so once seeded, the
    /// refresh itself never re-reads the manifest/vector files from disk — the same single load
    /// that would otherwise have happened inside a failed <see cref="IndexBuilder.RefreshAsync"/>
    /// call (and been thrown away with it) now happens once, up front, and survives the failure.
    /// </para>
    /// <para>
    /// Only rethrows when there is no snapshot to fall back to at all — neither one this instance
    /// already holds, nor one sitting on disk from a previous process (<see
    /// cref="SeedFromStoreIfEmptyAsync"/> found nothing, or the on-disk one is corrupted /
    /// incompatible with the current embedding model): with nothing indexed anywhere, there is
    /// nothing left to degrade to, so the failure is genuinely fatal and callers need to see it
    /// rather than silently get an empty result.
    /// </para>
    /// </remarks>
    private async Task<RefreshOutcome> RefreshOrFallBackAsync(CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            await SeedFromStoreIfEmptyAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            IndexSnapshot snapshot = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return new RefreshOutcome(snapshot, IndexStale: false, StaleWarning: null);
        }
        catch (EmbeddingUnavailableException ex)
        {
            IndexSnapshot? lastKnownGood = _current;
            if (lastKnownGood is null)
            {
                throw;
            }

            return new RefreshOutcome(lastKnownGood, IndexStale: true, StaleWarning: StaleIndexMessage(ex));
        }
    }

    /// <summary>
    /// Seeds <see cref="_current"/> from the on-disk store when this instance has never held a
    /// snapshot in memory — see the remarks on <see cref="RefreshOrFallBackAsync"/> for why this
    /// exists. Re-checks <see cref="_current"/> under <see cref="_gate"/> (not just before
    /// calling this method) so two overlapping first calls never both load the store: whichever
    /// wins the gate seeds it, and the other sees it already populated and does nothing. A no-op
    /// (nothing on disk, or what's there is corrupted/incompatible) leaves <see cref="_current"/>
    /// exactly as it was — still <c>null</c> — so the subsequent real refresh behaves exactly as
    /// it did before this method existed.
    /// </summary>
    private async Task SeedFromStoreIfEmptyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _current ??= await _builder.TryLoadStoredSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Warning text for "the index could not be refreshed" — distinct from, and more
    /// serious than, <see cref="RankingDegradedMessage"/>: the caller is looking at a snapshot of
    /// the past, not the current tree.</summary>
    private static string StaleIndexMessage(Exception ex) =>
        "Index is stale: the embedding backend is unavailable, so edits made since the last " +
        $"successful refresh are not reflected in these results. {ex.Message}";

    /// <summary>Warning text for "the query itself could not be embedded" — ranking falls back to
    /// symbol matches only, but the index contents shown are still current.</summary>
    private static string RankingDegradedMessage(Exception ex) =>
        "Semantic ranking is unavailable: the query could not be embedded, so results are " +
        $"symbol matches only. {ex.Message}";

    /// <summary>Joins a possible stale-index warning with a possible ranking-degraded warning —
    /// both can be true on the same call (a changed file forced a failed refresh attempt, and the
    /// query embedding then also failed), and the caller needs to see both reasons.</summary>
    private static string CombineWarnings(string? first, string second) =>
        first is null ? second : $"{first} {second}";

    /// <summary>Convenience wrapper over <see cref="SearchWithStatusAsync"/> for callers that do
    /// not need to distinguish a degraded (symbol-only) result from a fully hybrid one.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int limit, ChunkKind? kind, string? pathFilter, CancellationToken cancellationToken = default)
    {
        SearchResult result = await SearchWithStatusAsync(query, limit, kind, pathFilter, cancellationToken)
            .ConfigureAwait(false);
        return result.Hits;
    }

    /// <summary>
    /// Refreshes the index, then runs the hybrid search. Two independent things can degrade
    /// instead of throwing, and both are folded into <see cref="SearchResult.Warning"/>:
    /// <list type="bullet">
    /// <item><description>
    /// The mandatory refresh itself can fail with <see cref="EmbeddingUnavailableException"/>
    /// when a source file changed since the last successful refresh and its chunks need
    /// re-embedding. When that happens, this falls back to the last snapshot this instance
    /// successfully produced (see <see cref="RefreshOrFallBackAsync"/>) rather than failing the
    /// whole call. The resulting warning says the <em>index</em> is stale — edits since the last
    /// successful refresh are not reflected — which is more serious than a ranking note: the
    /// caller is looking at a snapshot of the past, not the current tree.
    /// </description></item>
    /// <item><description>
    /// The vector branch's own query embedding can separately fail even when the refresh above
    /// succeeded (nothing needed re-embedding). The resulting warning says semantic ranking is
    /// unavailable and results are symbol matches only — the index contents themselves are still
    /// current.
    /// </description></item>
    /// </list>
    /// Both can be true on the same call (a changed file forced a failed refresh attempt, and the
    /// query itself also could not be embedded); in that case both messages are present. The
    /// symbol branch always runs regardless of either failure — a stopped Ollama must leave this
    /// method returning useful matches, not an exception, unless the index has never been built at
    /// all (see <see cref="RefreshOrFallBackAsync"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="query"/> is blank, or <paramref
    /// name="limit"/> is negative — see the parameter checks at the top of this method for the
    /// exact messages. Checked before the (possibly expensive) refresh runs, so an invalid call
    /// never pays for one.</exception>
    public async Task<SearchResult> SearchWithStatusAsync(
        string query, int limit, ChunkKind? kind, string? pathFilter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // The symbol branch already treats a blank query as "matches nothing" (see
            // SymbolMatcher.Match), but the vector branch has no equivalent guard: it would
            // happily embed the bare instruction-prefix text and return whatever the embedding
            // backend considers closest to it — fifty confident-looking, entirely meaningless
            // hits. Rejecting outright here is cheaper (no refresh, no embedding call) and more
            // honest than letting either branch improvise an answer to a query that is not one.
            throw new ArgumentException("Query must not be blank.", nameof(query));
        }

        if (limit < 0)
        {
            // limit == 0 is a legitimate "give me nothing" call (handled below, after the
            // refresh, so its staleness warning is still reported); only negative is nonsensical.
            throw new ArgumentException($"{nameof(limit)} must not be negative, was {limit}.", nameof(limit));
        }

        RefreshOutcome refreshed = await RefreshOrFallBackAsync(cancellationToken).ConfigureAwait(false);
        IndexSnapshot snapshot = refreshed.Snapshot;

        if (limit == 0)
        {
            return new SearchResult([], refreshed.IndexStale, refreshed.StaleWarning);
        }

        // Each branch must be searched at least MinBranchDepth deep regardless of how small
        // `limit` is (see MinBranchDepth), but a caller asking for more final results than that
        // must not be silently capped at it — see the class remarks on BuildCandidateIndices for
        // the same "don't silently distort results" principle applied to filtering.
        int branchDepth = Math.Max(MinBranchDepth, limit);

        List<int>? candidateIndices = BuildCandidateIndices(snapshot, kind, pathFilter);

        IReadOnlyList<ScoredIndex> symbolHits = RunSymbolBranch(snapshot, candidateIndices, query, branchDepth);

        bool embeddingsUnavailable = refreshed.IndexStale;
        string? warning = refreshed.StaleWarning;
        IReadOnlyList<ScoredIndex> vectorHits = [];

        try
        {
            vectorHits = await RunVectorBranchAsync(snapshot, candidateIndices, query, branchDepth, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EmbeddingUnavailableException ex)
        {
            embeddingsUnavailable = true;
            warning = CombineWarnings(warning, RankingDegradedMessage(ex));
        }

        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(vectorHits, symbolHits, limit);

        // Excerpt reads are independent ISourceProvider calls (one file read each), so running
        // them concurrently instead of one-at-a-time keeps this linear in wall-clock file I/O
        // only for the slowest read, not the sum of all of them — worth doing now that `limit`
        // (and therefore fused.Count) is no longer capped at a small fixed branch depth.
        Task<string>[] excerptTasks = new Task<string>[fused.Count];
        for (int i = 0; i < fused.Count; i++)
        {
            excerptTasks[i] = ReadExcerptAsync(snapshot.Chunks[fused[i].Index], cancellationToken);
        }

        string[] excerpts = await Task.WhenAll(excerptTasks).ConfigureAwait(false);

        List<SearchHit> hits = new(fused.Count);
        for (int i = 0; i < fused.Count; i++)
        {
            ScoredIndex scored = fused[i];
            hits.Add(new SearchHit
            {
                ChunkId = scored.Index,
                Chunk = snapshot.Chunks[scored.Index],
                Score = scored.Score,
                Excerpt = excerpts[i],
            });
        }

        return new SearchResult(hits, embeddingsUnavailable, warning);
    }

    /// <summary>
    /// Reads the full body of one chunk by its ordinal id in the current snapshot. Like <see
    /// cref="SearchWithStatusAsync"/>, the mandatory refresh falls back to the last known-good
    /// snapshot when it fails with <see cref="EmbeddingUnavailableException"/> (see <see
    /// cref="RefreshOrFallBackAsync"/>) instead of failing outright — a chunk lookup against a
    /// slightly stale index is still useful, and there is no warning channel here to lose (unlike
    /// <see cref="SearchResult"/>, <see cref="SearchHit"/> carries no staleness flag). Returns
    /// <c>null</c> when <paramref name="chunkId"/> is out of range for whichever snapshot (fresh
    /// or stale) ends up in use — see the ordinal-id volatility warning on <see
    /// cref="IndexSnapshot"/>: an id obtained from one snapshot must never be looked up after a
    /// later refresh has changed chunk counts upstream of it.
    /// </summary>
    public async Task<SearchHit?> GetChunkAsync(int chunkId, CancellationToken cancellationToken = default)
    {
        RefreshOutcome refreshed = await RefreshOrFallBackAsync(cancellationToken).ConfigureAwait(false);
        IndexSnapshot snapshot = refreshed.Snapshot;

        if (chunkId < 0 || chunkId >= snapshot.Chunks.Count)
        {
            return null;
        }

        CodeChunk chunk = snapshot.Chunks[chunkId];
        string body = await ReadRangeAsync(chunk.FilePath, chunk.StartLine, chunk.EndLine, cancellationToken)
            .ConfigureAwait(false);

        return new SearchHit
        {
            ChunkId = chunkId,
            Chunk = chunk,
            Score = 0,
            Excerpt = body,
        };
    }

    /// <summary>
    /// Indices into <paramref name="snapshot"/>'s chunk list that satisfy both filters, in
    /// ascending order. Returns <c>null</c> — rather than the full <c>0..Count-1</c> range — when
    /// neither filter is set, so <see cref="RunSymbolBranch"/> and <see
    /// cref="RunVectorBranchAsync"/> can search the snapshot's own chunk list/vector array
    /// directly instead of building a filtered copy nobody asked for. See the class remarks for
    /// why filtering happens here, before either branch runs, rather than on their output.
    /// </summary>
    private static List<int>? BuildCandidateIndices(IndexSnapshot snapshot, ChunkKind? kind, string? pathFilter)
    {
        if (kind is null && string.IsNullOrEmpty(pathFilter))
        {
            return null;
        }

        IReadOnlyList<CodeChunk> chunks = snapshot.Chunks;
        List<int> candidates = new(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            CodeChunk chunk = chunks[i];

            if (kind is ChunkKind requiredKind && chunk.Kind != requiredKind)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(pathFilter) &&
                !chunk.FilePath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(i);
        }

        return candidates;
    }

    private static IReadOnlyList<ScoredIndex> RunSymbolBranch(
        IndexSnapshot snapshot, List<int>? candidateIndices, string query, int branchDepth)
    {
        if (candidateIndices is null)
        {
            return new SymbolMatcher(snapshot.Chunks).Match(query, branchDepth);
        }

        List<CodeChunk> filteredChunks = new(candidateIndices.Count);
        foreach (int index in candidateIndices)
        {
            filteredChunks.Add(snapshot.Chunks[index]);
        }

        IReadOnlyList<ScoredIndex> localHits = new SymbolMatcher(filteredChunks).Match(query, branchDepth);
        return RemapToOriginalIndices(localHits, candidateIndices);
    }

    /// <summary>
    /// Runs the vector branch and drops any candidate whose cosine similarity falls below <see
    /// cref="_minCosineSimilarity"/> — the relevance floor — before <paramref name="branchDepth"/>
    /// selection ever sees it (see <see cref="VectorSearcher.Search"/>'s <c>minScore</c>
    /// parameter). Without this floor, an unrelated project's best (but still weak) match, or a
    /// nonsense query's best (but still meaningless) match, would receive "its own rank 1" and
    /// score identically under Reciprocal Rank Fusion to a genuine top match elsewhere — RRF's
    /// score depends only on rank, never on how similar the match actually was. See <see
    /// cref="EmbeddingOptions.MinCosineSimilarity"/> for how the default threshold was measured.
    /// </summary>
    private async Task<IReadOnlyList<ScoredIndex>> RunVectorBranchAsync(
        IndexSnapshot snapshot, List<int>? candidateIndices, string query, int branchDepth, CancellationToken cancellationToken)
    {
        float[] queryVector = await _embedder.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        int dimensions = snapshot.Header.Dimensions;

        if (candidateIndices is null)
        {
            VectorSearcher searcher = new(snapshot.Vectors, dimensions);
            return searcher.Search(queryVector, branchDepth, _minCosineSimilarity);
        }

        float[] filteredVectors = new float[candidateIndices.Count * dimensions];
        for (int i = 0; i < candidateIndices.Count; i++)
        {
            snapshot.VectorAt(candidateIndices[i]).CopyTo(filteredVectors.AsSpan(i * dimensions, dimensions));
        }

        VectorSearcher filteredSearcher = new(filteredVectors, dimensions);
        IReadOnlyList<ScoredIndex> localHits = filteredSearcher.Search(queryVector, branchDepth, _minCosineSimilarity);
        return RemapToOriginalIndices(localHits, candidateIndices);
    }

    private static IReadOnlyList<ScoredIndex> RemapToOriginalIndices(
        IReadOnlyList<ScoredIndex> localHits, List<int> candidateIndices)
    {
        ScoredIndex[] remapped = new ScoredIndex[localHits.Count];
        for (int i = 0; i < localHits.Count; i++)
        {
            ScoredIndex local = localHits[i];
            remapped[i] = new ScoredIndex(candidateIndices[local.Index], local.Score);
        }

        return remapped;
    }

    /// <summary>Reads at most <see cref="MaxExcerptLines"/> lines starting at the chunk's first line.</summary>
    private async Task<string> ReadExcerptAsync(CodeChunk chunk, CancellationToken cancellationToken)
    {
        int endLine = Math.Min(chunk.EndLine, chunk.StartLine + MaxExcerptLines - 1);
        return await ReadRangeAsync(chunk.FilePath, chunk.StartLine, endLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an inclusive line range, returning an empty string instead of throwing when the
    /// file has moved or been deleted since indexing. Bodies are read fresh on every call (never
    /// cached alongside chunk metadata), so a transient read failure here should degrade this one
    /// result — the next <see cref="RefreshAsync"/> will drop the stale chunk entirely — not fail
    /// the whole search.
    /// </summary>
    private async Task<string> ReadRangeAsync(
        string filePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        try
        {
            return await _source.ReadLinesAsync(filePath, startLine, endLine, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
