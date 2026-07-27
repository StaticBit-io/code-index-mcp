namespace CodeIndex.Core.Embedding;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>
    /// Default for <see cref="MinCosineSimilarity"/> — see that property's remarks for how this
    /// value was measured. A separate <c>const</c> (rather than only living as the property's
    /// initialiser) so <see cref="CodeIndex.Core.Search.CodeIndexService"/>'s own constructor
    /// default can refer to the exact same value instead of duplicating the literal.
    /// </summary>
    public const double DefaultMinCosineSimilarity = 0.55;

    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3-embedding:4b";

    /// <summary>Truncation target. Qwen3-Embedding is MRL-trained, so 1024 keeps almost all quality.</summary>
    public int Dimensions { get; set; } = 1024;

    /// <summary>
    /// Sent as Ollama's <c>keep_alive</c> field on every <c>api/embed</c> request, controlling how
    /// long the model stays resident in VRAM after the call returns: a duration string
    /// (e.g. <c>"30m"</c>, <c>"1h"</c>), <c>"0"</c> to unload immediately, or <c>"-1"</c> to keep it
    /// loaded indefinitely. This model occupies about 10 GB of VRAM while resident, and Ollama's own
    /// default unloads it after 5 minutes idle — at a realistic search cadence (well under 5 minutes
    /// apart) that default turns every query into a ~12 s cold reload instead of the ~190 ms a warm
    /// model costs. <c>"-1"</c> would avoid that entirely but permanently starves a 16 GB card of the
    /// VRAM every other process needs; <c>"30m"</c> covers a normal working session's gaps between
    /// searches while still releasing the GPU once the session is clearly over.
    /// </summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>
    /// Task-instruction prefix applied only to the query side of a search — never to chunk/passage
    /// text — formatted as <c>"Instruct: {QueryInstruction}\nQuery: {query}"</c> before being sent
    /// to the embedding backend. Qwen3-Embedding (and other E5/GTE-family models) are trained
    /// asymmetrically: passages are encoded plain, but the model expects a query to carry an
    /// instruction naming the retrieval task, and omitting it measurably hurts ranking quality
    /// for exactly the kind of natural-language "how/where does X happen" query this tool exists
    /// to answer. See <see cref="Embedding.IEmbeddingClient.EmbedQueryAsync"/> for why this cannot
    /// be applied via the same code path as passage embedding. Set to <c>null</c> or empty to send
    /// the bare query with no prefix (e.g. when pointed at a model with no such training).
    /// </summary>
    public string? QueryInstruction { get; set; } =
        "Given a developer's question about a codebase, retrieve the C# code that implements it.";

    /// <summary>
    /// Relevance floor for the vector branch of a search: a chunk whose cosine similarity to the
    /// query falls below this value is excluded outright, never returned no matter how few other
    /// candidates cleared the floor. Passed through to <see
    /// cref="Search.VectorSearcher.Search(ReadOnlySpan{float}, int, float)"/>'s <c>minScore</c>
    /// parameter by <see cref="Search.CodeIndexService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a floor is needed at all.</b> <see cref="Search.VectorSearcher"/> always returns
    /// <c>min(topK, Count)</c> rows — whatever scored highest, however low that actually was. <see
    /// cref="Search.HybridRanker"/>'s Reciprocal Rank Fusion then scores purely by rank position
    /// within a branch, never by the underlying similarity value, so an unrelated project's (or an
    /// off-topic/nonsense query's) merely-best-available match still receives "rank 1" and scores
    /// identically to a genuinely strong match found elsewhere. Measured on a real project search
    /// merging a real index with an unrelated seven-chunk project: the unrelated project's best
    /// match scored cosine 0.071 against a query about trustline deletion, versus 0.9525 for the
    /// true hit in the real index — yet both fused to the exact same RRF score, because RRF never
    /// saw either raw number. A floor rejects the 0.071 before it ever reaches fusion, so it can
    /// no longer masquerade as a peer of the 0.9525 hit.
    /// </para>
    /// <para>
    /// <b>Where <c>0.55</c> (the default) comes from.</b> Measured against the project's own
    /// 8,751-chunk reference index (a 724-file C# SDK, <c>qwen3-embedding:4b</c>, 1024
    /// dimensions), rank-1 cosine similarity for 14 genuine developer queries ("where do we
    /// validate trustline deletion", "how is a payment transaction signed", "AMM pool trading
    /// fee", "escrow finish transaction", etc.) ranged <b>0.6116 to 0.9131</b>. Rank-1 cosine
    /// similarity for 11 queries with no genuine answer in the index (recipes, hiking, "asdkjfh
    /// aslkdjf qwoeiru zxcvnm", ...) ranged <b>0.3321 to 0.5402</b> — the highest of which was the
    /// gibberish query, which still landed near a generic <c>Program</c>/entry-point chunk purely
    /// because embedding models bias toward *some* nearest neighbour even for meaningless input.
    /// The two distributions never overlapped: every genuine query scored above 0.61, every
    /// off-topic one below 0.55. <c>0.55</c> sits in that gap — above every measured noise score
    /// (0.01 of margin above the worst case, the gibberish query) and comfortably below every
    /// measured genuine score (0.06 of margin below the weakest case, "retry logic for failed
    /// requests"). It is deliberately biased toward the noise side of the gap: a false negative
    /// (a real but weakly-worded match dropped) degrades gracefully back to the symbol branch and
    /// a rephrased query, while a false positive (noise let through) looks exactly like a genuine
    /// rank-1 hit with nothing in the response to tell them apart (see the <c>score</c> field on a
    /// <c>code_search</c> hit for the only signal that does survive).
    /// </para>
    /// <para>
    /// This is specific to <c>qwen3-embedding:4b</c> at 1024 dimensions with the default <see
    /// cref="QueryInstruction"/>: a different model, dimensionality, or instruction prefix shifts
    /// both distributions and may need a different threshold. Re-measure with the same method
    /// (rank-1 cosine for genuine vs. off-topic queries against a real index) before trusting this
    /// default on a different configuration.
    /// </para>
    /// </remarks>
    public double MinCosineSimilarity { get; set; } = DefaultMinCosineSimilarity;
}
