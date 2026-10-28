using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Integrations.IntegrationTests;

internal sealed record MetaRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Body);

/// <summary>Graph de mentira para el host entero: responde por patrón de ruta (regex sobre el path y la
/// query) en orden de registro; lo que no coincide responde 200 "{}". Anota cada request para que las
/// pruebas verifiquen qué se pidió y con qué token. Lo usan el signup y el probador de whatsapp-cloud:
/// ninguna prueba sale a graph.facebook.com.</summary>
internal sealed class FakeMetaGraphHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(Regex Pattern, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = new();

    public ConcurrentQueue<MetaRequest> Requests { get; } = new();

    public Exception? Throw { get; set; }

    public void Respond(string pathPattern, HttpStatusCode status, string body) =>
        _rules.Enqueue((new Regex(pathPattern, RegexOptions.CultureInvariant), _ => Json(status, body)));

    public void RespondWith(string pathPattern, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _rules.Enqueue((new Regex(pathPattern, RegexOptions.CultureInvariant), respond));

    public void Reset() => _rules.Clear();

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string GraphError(int code, int? subcode = null) =>
        $$$"""{"error":{"message":"x","type":"OAuthException","code":{{{code}}},"error_subcode":{{{(subcode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}}},"fbtrace_id":"trace"}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new MetaRequest(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        var target = request.RequestUri?.PathAndQuery ?? string.Empty;
        foreach (var (pattern, respond) in _rules)
        {
            if (pattern.IsMatch(target))
            {
                return respond(request);
            }
        }

        return Json(HttpStatusCode.OK, "{}");
    }
}
