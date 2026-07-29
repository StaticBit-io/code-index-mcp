using System.Net;
using CodeIndex.Core.Embedding;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Embedding;

public sealed class OllamaEmbeddingClientTests
{
    private static OllamaEmbeddingClient CreateClient(FakeHttpMessageHandler handler, int dimensions)
    {
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new() { Model = "qwen3-embedding:4b", Dimensions = dimensions };
        return new OllamaEmbeddingClient(http, Options.Create(options));
    }

    [Fact]
    public async Task EmbedAsync_TruncatesVectorToConfiguredDimensions()
    {
        // Qwen3-Embedding is MRL-trained, so truncating the tail is a supported operation.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,2,3,4,5,6]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        IReadOnlyList<float[]> result = await client.EmbedAsync(["text"], TestContext.Current.CancellationToken);

        Assert.Equal(4, result[0].Length);
    }

    [Fact]
    public async Task EmbedAsync_NormalisesVectorsToUnitLength()
    {
        // Pre-normalising lets search use a plain dot product later.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[3,4]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        IReadOnlyList<float[]> result = await client.EmbedAsync(["text"], TestContext.Current.CancellationToken);

        Assert.Equal(0.6f, result[0][0], tolerance: 0.0001f);
        Assert.Equal(0.8f, result[0][1], tolerance: 0.0001f);
    }

    [Fact]
    public async Task EmbedAsync_SendsModelAndInputInOneRequest()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0],[0,1]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        await client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains("qwen3-embedding:4b", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_SendsConfiguredKeepAliveInRequestBody()
    {
        // The model holds ~10 GB of VRAM while resident and Ollama unloads it after 5 minutes
        // idle by default; without an explicit keep_alive every query at a normal search cadence
        // would pay a ~12s cold reload instead of the ~190ms a warm model costs.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains("\"keep_alive\":\"30m\"", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_HonoursCustomKeepAliveValue()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new()
        {
            Model = "qwen3-embedding:4b",
            Dimensions = 2,
            KeepAlive = "-1",
        };
        OllamaEmbeddingClient client = new(http, Options.Create(options));

        await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains("\"keep_alive\":\"-1\"", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsEmbeddingUnavailableWhenOllamaIsDown()
    {
        FakeHttpMessageHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        EmbeddingUnavailableException error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("ollama serve", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsEmptyForEmptyInput()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        Assert.Empty(await client.EmbedAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(handler.CapturedBodies);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsEmbeddingUnavailableWhenModelIsMissing()
    {
        // A 404 from /api/embed means the model tag was never pulled. The message must name
        // the exact command, otherwise the user is left guessing what to run.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Failing(HttpStatusCode.NotFound);
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        EmbeddingUnavailableException error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("ollama pull qwen3-embedding:4b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenOllamaReturnsFewerVectorsThanInputs()
    {
        // A misaligned vector array would attach the wrong embedding to the wrong chunk,
        // silently corrupting the whole index. This must fail loudly instead.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenReturnedVectorIsShorterThanConfiguredDimensions()
    {
        // The model returned fewer values than the configured truncation target: there is
        // nothing to truncate, and padding with zeros would silently fabricate data.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,2]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_NormalisesZeroVectorWithoutProducingNaN()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[0,0,0,0]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        IReadOnlyList<float[]> result = await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.All(result[0], component => Assert.False(float.IsNaN(component)));
        Assert.All(result[0], component => Assert.Equal(0f, component));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenComponentIsSoLargeTheNormOverflowsToInfinity()
    {
        // 1e300 is already Infinity once read into a float; Infinity / Infinity is NaN, a
        // "unit" vector that would silently poison every comparison against it.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1e300,2,3,4]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenSquaringAComponentOverflowsTheNorm()
    {
        // 1e20 itself fits comfortably in a float, but its square (1e40) overflows float's
        // range during the sum-of-squares in Norm, turning the norm itself into Infinity.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1e20,2,3,4]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenComponentsUnderflowTheNormToZero()
    {
        // None of these components are zero, but each one squares down to zero in float
        // (1e-30 squared is 1e-60, far below float's smallest subnormal), so the norm itself
        // computes as zero. Treating that as a genuine zero vector would return an unnormalised
        // vector that silently breaks the "everything is unit length" contract search relies on.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1e-30,1e-30,1e-30,1e-30]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenAnEmbeddingElementIsNull()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[null]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_DistinguishesMissingEmbeddingsFieldFromCountMismatch()
    {
        // HTTP 200 with an error payload instead of embeddings (e.g. Ollama reporting a model
        // problem in the body rather than the status code) must not be misreported as "0
        // embeddings for 1 input" — that message would send the user chasing the wrong cause.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"error":"model not found"}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        EmbeddingUnavailableException error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.DoesNotContain("for 1 input", error.Message, StringComparison.Ordinal);
        Assert.Contains("'embeddings'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedQueryAsync_PrefixesQueryWithConfiguredInstruction()
    {
        // Qwen3-Embedding is trained asymmetrically: passages are encoded plain, but a query is
        // meant to carry an "Instruct: ...\nQuery: ..." prefix naming the retrieval task.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new()
        {
            Model = "qwen3-embedding:4b",
            Dimensions = 2,
            QueryInstruction = "Retrieve the C# member that answers the question.",
        };
        OllamaEmbeddingClient client = new(http, Options.Create(options));

        await client.EmbedQueryAsync("how is a payment signed", TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains(
            "Instruct: Retrieve the C# member that answers the question.\\nQuery: how is a payment signed",
            handler.CapturedBodies[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_NeverAppliesTheQueryInstructionToPassageText()
    {
        // The instruction must apply only to the query side. Mixing it into passage/chunk
        // embedding would require a full reindex and would defeat the asymmetry entirely.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new()
        {
            Model = "qwen3-embedding:4b",
            Dimensions = 2,
            QueryInstruction = "Retrieve the C# member that answers the question.",
        };
        OllamaEmbeddingClient client = new(http, Options.Create(options));

        await client.EmbedAsync(["public void Sign(string secret) { }"], TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.DoesNotContain("Instruct:", handler.CapturedBodies[0], StringComparison.Ordinal);
        Assert.Contains("public void Sign(string secret)", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedQueryAsync_SendsBareQueryWhenInstructionIsDisabled()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0]]}""");
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new()
        {
            Model = "qwen3-embedding:4b",
            Dimensions = 2,
            QueryInstruction = null,
        };
        OllamaEmbeddingClient client = new(http, Options.Create(options));

        await client.EmbedQueryAsync("how is a payment signed", TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.DoesNotContain("Instruct:", handler.CapturedBodies[0], StringComparison.Ordinal);
        Assert.Contains("how is a payment signed", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedQueryAsync_ReturnsTruncatedNormalisedVectorJustLikeEmbedAsync()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[3,4]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        float[] result = await client.EmbedQueryAsync("text", TestContext.Current.CancellationToken);

        Assert.Equal(0.6f, result[0], tolerance: 0.0001f);
        Assert.Equal(0.8f, result[1], tolerance: 0.0001f);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsEmbeddingUnavailableWhenRequestTimesOutWithoutUserCancellation()
    {
        // HttpClient reports its own request timeout as TaskCanceledException, indistinguishable
        // from user cancellation unless the caller's own token is checked. This matters in
        // practice: the first call to a freshly pulled model pays for loading it into memory
        // and can easily exceed a default timeout.
        FakeHttpMessageHandler handler = new(_ => throw new TaskCanceledException("simulated HttpClient timeout"));
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        EmbeddingUnavailableException error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }
}
