using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Anthropic de mentira para el adaptador: captura cada petición (cuerpo, headers) y responde lo que la prueba pida. Nunca sale a la red.
/// La clave capturada sólo se compara; ninguna prueba la imprime.
/// </summary>
internal sealed class FakeAnthropicServer : HttpMessageHandler
{
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    public Func<HttpResponseMessage> Respond { get; set; } = () => Ok(EndTurnBody("Hola."));

    public bool FailWithNetworkError { get; set; }

    public CapturedRequest Last => Requests.Last();

    public static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(int status, string type, string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(
                $$"""{"type":"error","error":{"type":"{{type}}","message":"m"},"request_id":"req_err"}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("request-id", "req_err");
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        }

        return response;
    }

    public static string EndTurnBody(string text, string model = "claude-opus-5-5") =>
        $$$"""
        {"id":"msg_1","type":"message","role":"assistant","model":"{{{model}}}","stop_reason":"end_turn","stop_sequence":null,
         "content":[{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}],
         "usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":80,"cache_read_input_tokens":0}}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWithNetworkError)
        {
            throw new HttpRequestException("connection refused");
        }

        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new CapturedRequest(
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
            request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : null,
            JsonDocument.Parse(body).RootElement.Clone()));
        return Respond();
    }

    public sealed record CapturedRequest(string Path, string? Bearer, string? ApiKeyHeader, string? Betas, JsonElement Body)
    {
        /// <summary>La clave con la que llegó la petición, venga por <c>Authorization: Bearer</c> (SDK 13.x) o por <c>x-api-key</c>.</summary>
        public string? Key => Bearer ?? ApiKeyHeader;
    }
}
