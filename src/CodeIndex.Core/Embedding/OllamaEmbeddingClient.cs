using System.Net;
using System.Net.Http.Headers;
using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CodeIndex.Core.Embedding;

/// <summary>
/// Talks to a local Ollama instance's <c>api/embed</c> endpoint. Vectors come back at the
/// model's native dimension and are truncated to <see cref="EmbeddingOptions.Dimensions"/> and
/// re-normalised to unit length before being handed back — Qwen3-Embedding is trained with
/// Matryoshka Representation Learning, so truncating the tail is a supported operation that
/// trades a few percent of retrieval quality for a smaller, faster-to-search index.
/// </summary>
public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private const int ErrorBodyPreviewLength = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;

    public OllamaEmbeddingClient(HttpClient httpClient, IOptions<EmbeddingOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public int Dimensions => _options.Dimensions;

    public string Model => _options.Model;

    /// <summary>
    /// Prepends <see cref="EmbeddingOptions.QueryInstruction"/> verbatim (when configured) and
    /// embeds the result via the same <see cref="EmbedAsync"/> path used for passages — the prefix
    /// is the only thing that distinguishes a query embedding from a passage embedding here, so
    /// there is nothing else for this method to do differently. <see
    /// cref="EmbeddingOptions.QueryInstruction"/> is a raw prefix, not a template: this method does
    /// not add any of its own formatting (no "Instruct:"/"Query:" labels), since different model
    /// families expect different prefix conventions — see that property's remarks.
    /// </summary>
    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        string text = string.IsNullOrEmpty(_options.QueryInstruction)
            ? query
            : $"{_options.QueryInstruction}{query}";

        IReadOnlyList<float[]> embedded = await EmbedAsync([text], cancellationToken).ConfigureAwait(false);
        return embedded[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        EmbedRequest request = new() { Model = _options.Model, Input = inputs, KeepAlive = _options.KeepAlive };
        string requestJson = JsonSerializer.Serialize(request, SerializerOptions);

        using HttpResponseMessage response = await PostAsync(requestJson, cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EmbedResponse parsed = ParseResponse(body);

        List<float[]?>? embeddings = parsed.Embeddings;
        if (embeddings is null)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama's response did not contain an 'embeddings' array. Body: {Preview(body)}");
        }

        if (embeddings.Count != inputs.Count)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama returned {embeddings.Count} embedding(s) for {inputs.Count} input(s). " +
                "The counts must match, otherwise chunks would be paired with the wrong vectors.");
        }

        float[][] result = new float[embeddings.Count][];
        for (int i = 0; i < embeddings.Count; i++)
        {
            float[]? vector = embeddings[i];
            if (vector is null)
            {
                throw new EmbeddingUnavailableException(
                    $"Ollama returned a null embedding for input {i}; a real vector was expected for every input.");
            }

            result[i] = TruncateAndNormalise(vector);
        }

        return result;
    }

    private async Task<HttpResponseMessage> PostAsync(string requestJson, CancellationToken cancellationToken)
    {
        using StringContent content = new(requestJson, Encoding.UTF8);
        content.Headers.ContentType = JsonMediaType;

        try
        {
            return await _httpClient.PostAsync("api/embed", content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingUnavailableException(
                $"Could not reach Ollama at {_httpClient.BaseAddress}. Start it with 'ollama serve' and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own request timeout as a TaskCanceledException, not as an
            // HttpRequestException. The first call against a freshly pulled model is the most
            // likely trigger: Ollama has to load several gigabytes into memory before it can
            // answer, which can take minutes and easily outlast a default HttpClient timeout.
            throw new EmbeddingUnavailableException(
                $"The request to Ollama at {_httpClient.BaseAddress} timed out. The first request to " +
                $"'{_options.Model}' can take minutes while Ollama loads the model into memory — " +
                "either wait and retry, warm it up first with 'ollama run " + _options.Model + "', " +
                "or increase HttpClient.Timeout.", ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama model '{_options.Model}' is not installed. Run 'ollama pull {_options.Model}' and try again.");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new EmbeddingUnavailableException(
            $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase} from api/embed. Body: {Preview(body)}");
    }

    private static EmbedResponse ParseResponse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<EmbedResponse>(body, SerializerOptions)
                ?? throw new EmbeddingUnavailableException("Ollama returned an empty response body for api/embed.");
        }
        catch (JsonException ex)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama returned a response that could not be parsed as JSON from api/embed. Body: {Preview(body)}", ex);
        }
    }

    /// <summary>
    /// Truncates a native-dimension embedding to <see cref="EmbeddingOptions.Dimensions"/> and
    /// normalises it to unit length so search can later use a plain dot product.
    /// </summary>
    private float[] TruncateAndNormalise(float[] embedding)
    {
        if (embedding.Length < _options.Dimensions)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama model '{_options.Model}' returned a {embedding.Length}-dimensional vector, " +
                $"shorter than the configured {_options.Dimensions}. Lower EmbeddingOptions.Dimensions " +
                "to match the model, or switch to a model with at least that many native dimensions.");
        }

        float[] truncated = embedding.Length == _options.Dimensions ? embedding : embedding[.._options.Dimensions];

        float norm = TensorPrimitives.Norm<float>(truncated);

        if (!float.IsFinite(norm))
        {
            // A component large enough to overflow float during squaring (or during the JSON
            // read itself) turns the norm into Infinity, and Infinity/Infinity is NaN — a
            // "unit" vector that would silently poison every dot product it is compared with.
            throw new EmbeddingUnavailableException(
                $"Ollama model '{_options.Model}' returned a vector with a non-finite norm ({norm}); " +
                "the response likely contains an out-of-range or corrupted component.");
        }

        if (norm == 0f)
        {
            // Norm computes sum-of-squares first: components far smaller than 1 but not
            // actually zero (e.g. 1e-30) can square down to zero in float and make a non-zero
            // vector look zero. TensorPrimitives.MaxMagnitude checks raw component magnitude
            // (no squaring), so it still tells a genuine zero vector apart from underflow.
            float maxMagnitude = TensorPrimitives.MaxMagnitude<float>(truncated);
            if (maxMagnitude == 0f)
            {
                return truncated;
            }

            throw new EmbeddingUnavailableException(
                $"Ollama model '{_options.Model}' returned a vector whose components are too small to " +
                "normalise reliably: they underflowed the norm to zero without being an actual zero vector.");
        }

        float[] normalised = new float[truncated.Length];
        TensorPrimitives.Divide(truncated, norm, normalised);
        return normalised;
    }

    private static string Preview(string body) =>
        body.Length <= ErrorBodyPreviewLength ? body : body[..ErrorBodyPreviewLength];

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<string> Input { get; init; }

        [JsonPropertyName("keep_alive")]
        public required string KeepAlive { get; init; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]?>? Embeddings { get; init; }
    }
}
