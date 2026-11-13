using System.Collections.Concurrent;
using System.Net;

namespace Modules.Ai.IntegrationTests;

/// <summary>Anthropic de mentira: anota cada request (método, URL y si trajo la clave esperada, nunca la clave) y responde el status que
/// la prueba pida.</summary>
internal sealed class FakeAnthropicHandler(string expectedKey) : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public ConcurrentQueue<(HttpMethod Method, string? Url, bool KeyMatched)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
        Requests.Enqueue((request.Method, request.RequestUri?.ToString(), key == expectedKey));
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("""{"data":[]}""") });
    }
}
