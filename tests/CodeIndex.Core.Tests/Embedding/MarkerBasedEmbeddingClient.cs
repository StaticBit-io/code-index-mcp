using CodeIndex.Core.Embedding;

namespace CodeIndex.Core.Tests.Embedding;

/// <summary>
/// A fully controllable stand-in for a real embedding backend, used to test the relevance floor
/// (<see cref="EmbeddingOptions.MinCosineSimilarity"/>) with exact, known cosine similarities
/// instead of the essentially-random vectors <see cref="StubEmbeddingClient"/> produces (which
/// cannot reliably land on either side of a threshold). Every query embeds to a fixed "aligned"
/// direction; a passage embeds to that same direction only when its text contains <see
/// cref="RealHitMarker"/>, to a direction orthogonal to it when it contains <see
/// cref="UnrelatedHitMarker"/>, and to a direction 45 degrees off (cosine ~0.707 against the
/// query — deliberately mediocre, neither a clean hit nor clean noise) for anything else. Two
/// dimensions is enough: the class only ever needs three distinguishable directions.
/// </summary>
public sealed class MarkerBasedEmbeddingClient : IEmbeddingClient
{
    /// <summary>Include this token in a chunk's symbol/signature/body (see
    /// <c>RoslynChunker.BuildEmbedText</c>) to make that chunk embed to the exact same direction
    /// as every query — cosine similarity 1.0, an unambiguous genuine hit.</summary>
    public const string RealHitMarker = "REALHITMARKER";

    /// <summary>Include this token to make a chunk embed orthogonally to every query — cosine
    /// similarity 0.0, an unambiguous non-match regardless of any relevance floor chosen.</summary>
    public const string UnrelatedHitMarker = "UNRELATEDHITMARKER";

    private static readonly float[] AlignedVector = [1f, 0f];
    private static readonly float[] OrthogonalVector = [0f, 1f];
    private static readonly float[] MediocreVector = [0.70710678f, 0.70710678f];

    public int Dimensions => 2;

    public string Model => "marker-based-test-model";

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        float[][] vectors = new float[inputs.Count][];
        for (int i = 0; i < inputs.Count; i++)
        {
            vectors[i] = VectorFor(inputs[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>Every query embeds to <see cref="AlignedVector"/> regardless of its text — the
    /// query side never needs to vary for these tests, only the passage side does.</summary>
    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(AlignedVector);

    private static float[] VectorFor(string text)
    {
        if (text.Contains(RealHitMarker, StringComparison.Ordinal))
        {
            return AlignedVector;
        }

        if (text.Contains(UnrelatedHitMarker, StringComparison.Ordinal))
        {
            return OrthogonalVector;
        }

        return MediocreVector;
    }
}
