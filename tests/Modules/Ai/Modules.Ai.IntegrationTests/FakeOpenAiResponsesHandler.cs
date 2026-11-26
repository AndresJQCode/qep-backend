using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// La Responses API de mentira para el adaptador de OpenAI: responde un guion de cuerpos, en orden. De cada petición anota la ruta, si
/// trajo la clave esperada como <c>Bearer</c> (nunca la clave) y si pidió <c>store: false</c>.
/// </summary>
internal sealed class FakeOpenAiResponsesHandler(string expectedKey) : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _script = new();

    public ConcurrentQueue<(string Path, bool KeyMatched, bool Stateless)> Requests { get; } = new();

    public void Enqueue(string body) => _script.Enqueue(body);

    public static string FunctionCall(string callId, string name, string argumentsJson) =>
        $$$"""
        {"id":"resp_tool","object":"response","status":"completed","model":"gpt-6-luna","incomplete_details":null,"error":null,
         "output":[{"type":"reasoning","id":"rs_fake","summary":[],"encrypted_content":"enc-fake"},
                   {"type":"function_call","id":"fc_fake","call_id":"{{{callId}}}","name":"{{{name}}}","arguments":{{{JsonSerializer.Serialize(argumentsJson)}}},"status":"completed"}],
         "usage":{"input_tokens":1500,"input_tokens_details":{"cached_tokens":0},"output_tokens":40,"total_tokens":1540}}
        """;

    public static string Text(string text) =>
        $$$"""
        {"id":"resp_end","object":"response","status":"completed","model":"gpt-6-luna","incomplete_details":null,"error":null,
         "output":[{"type":"message","id":"msg_end","status":"completed","role":"assistant",
                    "content":[{"type":"output_text","text":{{{JsonSerializer.Serialize(text)}}},"annotations":[]}]}],
         "usage":{"input_tokens":1600,"input_tokens_details":{"cached_tokens":1400},"output_tokens":25,"total_tokens":1625}}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var stateless = document.RootElement.TryGetProperty("store", out var store) && store.ValueKind == JsonValueKind.False;
        Requests.Enqueue((
            request.RequestUri?.AbsolutePath ?? string.Empty,
            authorization?.Scheme == "Bearer" && authorization.Parameter == expectedKey,
            stateless));
        var next = _script.TryDequeue(out var scripted) ? scripted : Text("Listo.");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(next, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", "req_fake");
        return response;
    }
}
