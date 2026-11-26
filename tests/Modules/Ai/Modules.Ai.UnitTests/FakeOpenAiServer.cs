using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Modules.Ai.UnitTests;

/// <summary>
/// La Responses API de OpenAI, de mentira, para el adaptador: captura cada petición (ruta, clave, cuerpo) y responde lo que la prueba pida.
/// Nunca sale a la red. La clave capturada sólo se compara; ninguna prueba la imprime.
/// </summary>
internal sealed class FakeOpenAiServer : HttpMessageHandler
{
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    public Func<HttpResponseMessage> Respond { get; set; } = () => Ok(TextBody("Hola."));

    public Exception? Throw { get; set; }

    public CapturedRequest Last => Requests.Last();

    public static HttpResponseMessage Ok(string body, string requestId = "req_ok")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        return response;
    }

    /// <summary>La forma de error de la API: <c>{"error":{"message","type","param","code"}}</c> (guía de códigos de error).</summary>
    public static HttpResponseMessage Error(int status, string? type, string? code, string? retryAfter = null, string message = "m")
    {
        var error = JsonSerializer.Serialize(new { error = new { message, type, param = (string?)null, code } });
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(error, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", "req_err");
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        }

        return response;
    }

    /// <summary>Una respuesta completa con sólo un mensaje de texto del asistente.</summary>
    public static string TextBody(string text, string model = "gpt-6-luna") =>
        $$$"""
        {"id":"resp_1","object":"response","status":"completed","model":"{{{model}}}","incomplete_details":null,"error":null,
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
                    "content":[{"type":"output_text","text":{{{JsonSerializer.Serialize(text)}}},"annotations":[]}]}],
         "usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":0},"output_tokens":20,
                  "output_tokens_details":{"reasoning_tokens":5},"total_tokens":120}}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Throw is { } failure)
        {
            throw failure;
        }

        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new CapturedRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            JsonDocument.Parse(body).RootElement.Clone()));
        return Respond();
    }

    public sealed record CapturedRequest(HttpMethod Method, string Url, string? Scheme, string? Key, JsonElement Body);
}
