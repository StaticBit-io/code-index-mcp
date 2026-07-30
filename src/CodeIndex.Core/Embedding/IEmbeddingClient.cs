namespace CodeIndex.Core.Embedding;

/// <summary>
/// Turns chunk text into unit-length embedding vectors. Implementations are expected to do
/// this locally (no source code should ever leave the machine to compute an embedding).
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Length of every vector this client returns, after any truncation.</summary>
    int Dimensions { get; }

    /// <summary>Identifier of the embedding model backing this client.</summary>
    string Model { get; }

    /// <summary>
    /// Embeds every input and returns one unit-length vector per input, in the same order.
    /// Callers are responsible for batching; this method sends whatever it is given as a
    /// single request. Used exclusively for passage/chunk text — see <see cref="EmbedQueryAsync"/>
    /// for embedding a search query.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds a single search query and returns its unit-length vector.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate method from <see cref="EmbedAsync"/> rather than an extra parameter
    /// on it: some embedding models (Qwen3-Embedding among them) are trained asymmetrically —
    /// passages are encoded plain, but a query is meant to carry a short prefix (see <see
    /// cref="EmbeddingOptions.QueryInstruction"/>). Every call site in this codebase
    /// already embeds queries one at a time and passages in batches, so a dedicated method matches
    /// how the two are actually used, and makes it structurally impossible for passage text to
    /// pick up a query instruction by accident (or vice versa) — there is no shared code path
    /// where a caller could pass the wrong flag. Implementations with no such asymmetry are free
    /// to implement this by simply delegating to <see cref="EmbedAsync"/> with the query
    /// unchanged.
    /// </remarks>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown whenever an embedding could not be produced: the embedding server is unreachable,
/// the configured model is not installed, or the response could not be trusted (wrong shape,
/// wrong vector count). Callers should surface the message directly — it is written to name
/// the exact remedy (e.g. the command to run) rather than describe the symptom.
/// </summary>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message) : base(message)
    {
    }

    public EmbeddingUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
