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
    private readonly ConcurrentQueue<(Regex Pattern, string? BodyContains, Func<string?, HttpResponseMessage> Respond, TimeSpan Delay)> _rules = new();

    public ConcurrentQueue<ShopifyRequest> Requests { get; } = new();

    /// <param name="delay">Lo que tarda Shopify en responder: con él, dos requests concurrentes se cruzan de verdad.</param>
    public void Respond(string pattern, HttpStatusCode status, string body, TimeSpan delay = default) =>
        _rules.Enqueue((new Regex(pattern, RegexOptions.CultureInvariant), null, _ => Json(status, body), delay));

    /// <summary>Como <see cref="Respond"/>, pero sólo si el cuerpo contiene <paramref name="bodyContains"/>: la Admin API es
    /// una sola URL de GraphQL y lo que distingue la consulta es el cuerpo. Se registra antes que una regla sin cuerpo.</summary>
    public void RespondWhen(string pattern, string bodyContains, Func<string?, HttpResponseMessage> respond) =>
        _rules.Enqueue((new Regex(pattern, RegexOptions.CultureInvariant), bodyContains, respond, TimeSpan.Zero));

    /// <summary>
    /// Las suscripciones de la app en <paramref name="shop"/> (<paramref name="existingTopics"/>, con el enum de GraphQL,
    /// hacia <paramref name="callback"/>) y el alta de las demás: cada alta responde el id
    /// <c>gid://shopify/WebhookSubscription/{TOPIC}</c>, o el <c>userErrors</c> si <paramref name="rejectCreate"/>. Va antes de
    /// <see cref="ScriptHappyInstall"/>, cuya regla de GraphQL no mira el cuerpo.
    /// </summary>
    public void ScriptWebhooks(string shop, string callback, bool rejectCreate = false, params string[] existingTopics)
    {
        var graphQl = $"^{Regex.Escape(shop)}/admin/api/[0-9-]+/graphql\\.json$";
        var edges = string.Join(',', existingTopics.Select(topic =>
            $$$"""{"node":{"id":"gid://shopify/WebhookSubscription/{{{topic}}}","topic":"{{{topic}}}","uri":"{{{callback}}}"}}"""));
        RespondWhen(graphQl, "webhookSubscriptions(first", _ =>
            Json(HttpStatusCode.OK, $$$$"""{"data":{"webhookSubscriptions":{"edges":[{{{{edges}}}}]}}}"""));
        RespondWhen(graphQl, "webhookSubscriptionCreate", body =>
        {
            if (rejectCreate)
            {
                return Json(
                    HttpStatusCode.OK,
                    """{"data":{"webhookSubscriptionCreate":{"webhookSubscription":null,"userErrors":[{"field":["callbackUrl"],"message":"Address is invalid"}]}}}""");
            }

            var topic = Regex.Match(body ?? string.Empty, "\"topic\":\"([A-Z_]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
            return Json(
                HttpStatusCode.OK,
                $$$$"""{"data":{"webhookSubscriptionCreate":{"webhookSubscription":{"id":"gid://shopify/WebhookSubscription/{{{{topic}}}}","topic":"{{{{topic}}}}","uri":"{{{{callback}}}}"},"userErrors":[]}}}""");
        });
    }

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
        foreach (var (pattern, bodyContains, respond, delay) in _rules)
        {
            if (pattern.IsMatch(target) && (bodyContains is null || (body?.Contains(bodyContains, StringComparison.Ordinal) ?? false)))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return respond(body);
            }
        }

        return Json(HttpStatusCode.NotFound, """{"errors":"Not Found"}""");
    }
}
