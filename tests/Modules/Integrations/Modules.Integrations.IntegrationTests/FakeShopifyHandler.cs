using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Integrations.IntegrationTests;

internal sealed record ShopifyRequest(HttpMethod Method, Uri? Uri, string? AccessToken, string? Body);

/// <summary>Shopify de mentira para el host entero, como <see cref="FakeMetaGraphHandler"/>: responde por patrón (regex
/// sobre host + path) en orden de registro, y lo que no coincide responde 404. Anota cada request con su
/// <c>X-Shopify-Access-Token</c> para que las pruebas verifiquen qué se pidió y con qué token. Ninguna prueba sale a una
/// tienda real (V-1, V-3 y V-7 se cierran contra Shopify, en el handoff).</summary>
internal sealed class FakeShopifyHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(Regex Pattern, Func<HttpRequestMessage, HttpResponseMessage> Respond, TimeSpan Delay)> _rules = new();

    public ConcurrentQueue<ShopifyRequest> Requests { get; } = new();

    /// <param name="delay">Lo que tarda Shopify en responder: con él, dos requests concurrentes se cruzan de verdad.</param>
    public void Respond(string pattern, HttpStatusCode status, string body, TimeSpan delay = default) =>
        _rules.Enqueue((new Regex(pattern, RegexOptions.CultureInvariant), _ => Json(status, body), delay));

    public void Reset() => _rules.Clear();

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>El canje y la lectura de la tienda de una instalación sana de <paramref name="shop"/>.</summary>
    public void ScriptHappyInstall(
        string shop, string accessToken, string scopes = "read_orders,read_customers", string name = "Mi Tienda", TimeSpan exchangeDelay = default)
    {
        Respond(
            $"^{Regex.Escape(shop)}/admin/oauth/access_token$",
            HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}","scope":"{{scopes}}"}""",
            exchangeDelay);
        Respond(
            $"^{Regex.Escape(shop)}/admin/api/[0-9-]+/graphql\\.json$",
            HttpStatusCode.OK,
            $$$$"""{"data":{"shop":{"id":"gid://shopify/Shop/12345678","name":"{{{{name}}}}","myshopifyDomain":"{{{{shop}}}}"}}}""");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new ShopifyRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-Shopify-Access-Token", out var values) ? values.Single() : null,
            body));

        var target = request.RequestUri is { } uri ? uri.Host + uri.AbsolutePath : string.Empty;
        foreach (var (pattern, respond, delay) in _rules)
        {
            if (pattern.IsMatch(target))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return respond(request);
            }
        }

        return Json(HttpStatusCode.NotFound, """{"errors":"Not Found"}""");
    }
}
