using System.Net;

namespace CodeIndex.Core.Tests.Embedding;

/// <summary>
/// Hand-written stand-in for a mocking library: the canned response is visible right
/// next to the assertion instead of hidden behind a setup DSL.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<string> CapturedBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public static FakeHttpMessageHandler Returning(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    public static FakeHttpMessageHandler Failing(HttpStatusCode code) =>
        new(_ => new HttpResponseMessage(code) { Content = new StringContent("{}") });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            CapturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return _responder(request);
    }
}
