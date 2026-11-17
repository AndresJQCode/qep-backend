using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// La Messages API de mentira para el adaptador de Claude: responde un guion de cuerpos, en orden, y puede quedarse esperando
/// (<see cref="Gate"/>) para que la prueba vea un trabajo en curso. De cada petición anota sólo si trajo la clave esperada, nunca la clave.
/// </summary>
internal sealed class FakeAnthropicMessagesHandler(string expectedKey) : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _script = new();

    public SemaphoreSlim? Gate { get; set; }

    public ConcurrentQueue<(string Path, bool KeyMatched)> Requests { get; } = new();

    public void Enqueue(string body) => _script.Enqueue(body);

    public static string ToolUse(string id, string name, string inputJson) =>
        $$$"""
        {"id":"msg_tool","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"tool_use","stop_sequence":null,
         "content":[{"type":"thinking","thinking":"","signature":"sig-fake"},{"type":"tool_use","id":"{{{id}}}","name":"{{{name}}}","input":{{{inputJson}}}}],
         "usage":{"input_tokens":1500,"output_tokens":40,"cache_creation_input_tokens":1400,"cache_read_input_tokens":0}}
        """;

    public static string EndTurn(string text) =>
        $$$"""
        {"id":"msg_end","type":"message","role":"assistant","model":"claude-opus-5","stop_reason":"end_turn","stop_sequence":null,
         "content":[{"type":"text","text":"{{{text}}}"}],
         "usage":{"input_tokens":1600,"output_tokens":25,"cache_creation_input_tokens":0,"cache_read_input_tokens":1400}}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.Authorization?.Parameter
            ?? (request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null);
        Requests.Enqueue((request.RequestUri?.AbsolutePath ?? string.Empty, key == expectedKey));
        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        var body = _script.TryDequeue(out var next) ? next : EndTurn("Listo.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
