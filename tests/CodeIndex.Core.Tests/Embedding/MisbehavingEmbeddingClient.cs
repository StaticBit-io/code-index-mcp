using CodeIndex.Core.Embedding;

namespace CodeIndex.Core.Tests.Embedding;

public enum MisbehaviorMode
{
    /// <summary>Behaves exactly like <see cref="StubEmbeddingClient"/>.</summary>
    None,

    /// <summary>Returns one fewer vector than the batch it was given — the "compensating batch
    /// error" scenario: a correct total count masking a shift within the batch.</summary>
    DropOneVector,

    /// <summary>Returns a vector one component short of <see cref="MisbehavingEmbeddingClient.Dimensions"/>
    /// for the first input of every batch.</summary>
    ShortVector,
}

/// <summary>
/// Wraps <see cref="StubEmbeddingClient"/> but deliberately violates the
/// <see cref="IEmbeddingClient.EmbedAsync"/> contract in one configurable way, to prove that
/// <c>IndexBuilder</c> does not trust an embedding backend's response shape without checking it.
/// </summary>
public sealed class MisbehavingEmbeddingClient : IEmbeddingClient
{
    private readonly StubEmbeddingClient _inner = new();

    public required MisbehaviorMode Mode { get; init; }

    public int Dimensions => _inner.Dimensions;

    public string Model => _inner.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> result = await _inner.EmbedAsync(inputs, cancellationToken).ConfigureAwait(false);

        if (result.Count == 0)
        {
            return result;
        }

        return Mode switch
        {
            MisbehaviorMode.DropOneVector => result.Take(result.Count - 1).ToList(),
            MisbehaviorMode.ShortVector => Shorten(result),
            _ => result,
        };
    }

    private static List<float[]> Shorten(IReadOnlyList<float[]> result)
    {
        List<float[]> copy = new(result);
        copy[0] = copy[0][..^1];
        return copy;
    }

    /// <summary>Not exercised by <see cref="Indexing.IndexBuilderTests"/> (which only ever embeds
    /// passages), but required to satisfy <see cref="IEmbeddingClient"/>. Delegates to this
    /// class's own <see cref="EmbedAsync"/> so the configured <see cref="Mode"/> still applies if
    /// a future test does exercise it.</summary>
    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> result = await EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        return result[0];
    }
}
