namespace CodeIndex.Core.Embedding;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

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
}
