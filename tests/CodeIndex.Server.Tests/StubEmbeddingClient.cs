using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using CodeIndex.Core.Embedding;

namespace CodeIndex.Server.Tests;

/// <summary>
/// Deterministic, network-free stand-in for a real embedding backend: the vector for a given
/// input string is a pure function of that string's SHA-256 hash, so embedding the same text
/// twice always returns the same unit-length vector. Toggling <see cref="ShouldThrow"/> lets a
/// test simulate the embedding backend going down after the index was already built with working
/// embeddings, without needing a second class.
/// </summary>
public sealed class StubEmbeddingClient : IEmbeddingClient
{
    public int Dimensions => 4;

    public string Model => "server-tests-stub-model";

    public bool ShouldThrow { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (ShouldThrow)
        {
            throw new EmbeddingUnavailableException("Stub embedding backend is unavailable.");
        }

        float[][] vectors = new float[inputs.Count][];
        for (int i = 0; i < inputs.Count; i++)
        {
            vectors[i] = Embed(inputs[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>No asymmetry to model here: delegates straight to <see cref="EmbedAsync"/> (and so
    /// shares its <see cref="ShouldThrow"/> behaviour) with the query text unchanged.</summary>
    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> result = await EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    private float[] Embed(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        float[] vector = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
        {
            uint bits = BitConverter.ToUInt32(hash, i * 4);
            vector[i] = bits / (float)uint.MaxValue * 2f - 1f;
        }

        float norm = TensorPrimitives.Norm<float>(vector);
        if (norm == 0f)
        {
            vector[0] = 1f;
            return vector;
        }

        float[] normalised = new float[Dimensions];
        TensorPrimitives.Divide(vector, norm, normalised);
        return normalised;
    }
}
