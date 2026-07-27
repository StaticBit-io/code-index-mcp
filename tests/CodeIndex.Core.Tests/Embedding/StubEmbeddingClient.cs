using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using CodeIndex.Core.Embedding;

namespace CodeIndex.Core.Tests.Embedding;

/// <summary>
/// Deterministic, network-free stand-in for a real embedding backend: the vector for a given
/// input string is a pure function of that string's SHA-256 hash, so calling
/// <see cref="EmbedAsync"/> twice with the same text always returns the same unit-length
/// vector. That determinism is what lets <see cref="Indexing.IndexBuilderTests"/> assert both
/// "this file was not re-embedded" (its stored vector still equals a fresh embedding of its
/// text) and exact vector/chunk alignment after a partial refresh, without needing to intercept
/// or record individual calls.
/// </summary>
public sealed class StubEmbeddingClient : IEmbeddingClient
{
    public int Dimensions => 4;

    public string Model => "stub-model";

    /// <summary>Number of times <see cref="EmbedAsync"/> was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>Total number of input strings embedded across every call.</summary>
    public int TotalInputs { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        CallCount++;
        TotalInputs += inputs.Count;

        float[][] vectors = new float[inputs.Count][];
        for (int i = 0; i < inputs.Count; i++)
        {
            vectors[i] = Embed(inputs[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>No asymmetry to model here: delegates straight to <see cref="EmbedAsync"/> with
    /// the query text unchanged, same as any other input.</summary>
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
            // Astronomically unlikely for a real SHA-256 output, but keep the contract ("unit
            // length, always") true rather than dividing by zero.
            vector[0] = 1f;
            return vector;
        }

        float[] normalised = new float[Dimensions];
        TensorPrimitives.Divide(vector, norm, normalised);
        return normalised;
    }
}
