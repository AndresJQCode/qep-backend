using System.Collections.Concurrent;
using System.Net;

namespace Modules.Ai.IntegrationTests;

/// <summary>El <c>GET /v1/models</c> de OpenAI, de mentira: anota cada request (método, URL y si trajo la clave esperada como
/// <c>Bearer</c>, nunca la clave) y responde el status que la prueba pida.</summary>
internal sealed class FakeOpenAiModelsHandler(string expectedKey) : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public ConcurrentQueue<(HttpMethod Method, string? Url, bool KeyMatched)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization;
        Requests.Enqueue((request.Method, request.RequestUri?.ToString(), authorization?.Scheme == "Bearer" && authorization.Parameter == expectedKey));
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("""{"object":"list","data":[]}""") });
    }
}
