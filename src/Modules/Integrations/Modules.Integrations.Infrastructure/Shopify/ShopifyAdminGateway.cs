using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.3 y §4.1 contra Shopify, sobre <c>integrations.shopify-admin</c> con el patrón de
/// <c>MetaGraphClient</c>: sin redirecciones (un 3xx no lleva el token a otro host), 10 s, sin los loggers de headers.
/// No tiene <c>BaseAddress</c>: el host es la tienda. El canje va a <c>https://{shop}/admin/oauth/access_token</c> y la Admin
/// API a <c>https://{shop}/admin/api/{ApiVersion}/graphql.json</c> con <c>X-Shopify-Access-Token</c>. Registra la operación
/// y el status; nunca el cuerpo, el token, el <c>code</c> ni el client secret.
/// </summary>
internal sealed partial class ShopifyAdminGateway(
    IHttpClientFactory httpClientFactory,
    IShopifyAppSettings shopifyApp,
    ILogger<ShopifyAdminGateway> logger) : IShopifyAdminGateway
{
    public const string HttpClientName = "integrations.shopify-admin";

    internal const string ShopQuery = "{ shop { id name myshopifyDomain } }";

    internal const string WebhooksQuery = "{ webhookSubscriptions(first: 50) { edges { node { id topic uri } } } }";

    internal const string CreateWebhookMutation =
        "mutation webhookSubscriptionCreate($topic: WebhookSubscriptionTopic!, $webhookSubscription: WebhookSubscriptionInput!) "
        + "{ webhookSubscriptionCreate(topic: $topic, webhookSubscription: $webhookSubscription) "
        + "{ webhookSubscription { id topic uri } userErrors { field message } } }";

    private const string ShopGidPrefix = "gid://shopify/Shop/";

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopify {Operation} answered HTTP {StatusCode}")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify {Operation} answered something unusable: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string operation, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Lo que espera cada llamada. El barrido de instalaciones (<see cref="ShopifyInstallSweeper"/>) le da margen
    /// a un canje en vuelo con esto.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public async Task<ShopifyTokenGrant> ExchangeCodeAsync(string shop, string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{Host(shop)}/admin/oauth/access_token", UriKind.Absolute))
        {
            Content = JsonContent.Create(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = shopifyApp.ClientId ?? string.Empty,
                ["client_secret"] = shopifyApp.ClientSecret ?? string.Empty,
                ["code"] = code,
            }),
        };
        using var document = await SendAsync("oauth", request, accessToken: null, cancellationToken);
        var root = document.RootElement;
        var token = ReadString(root, "access_token");
        var scopes = ReadString(root, "scope");
        if (string.IsNullOrEmpty(token) || scopes is null)
        {
            throw Invalid("oauth", "no_token");
        }

        if (scopes.Length > ShopifyInstall.ScopesMaxLength)
        {
            throw Invalid("oauth", "scopes_too_long");
        }

        return new ShopifyTokenGrant(token, scopes);
    }

    public async Task<ShopifyShopInfo> GetShopAsync(string shop, string accessToken, CancellationToken cancellationToken)
    {
        using var document = await GraphQlAsync("shop", shop, accessToken, ShopQuery, variables: null, cancellationToken);
        if (!TryData(document.RootElement, "shop", out var node) || ReadString(node, "id") is not { Length: > 0 } gid)
        {
            throw Invalid("shop", "no_shop");
        }

        // Los webhooks traen el id numérico; GraphQL, el GID. Se guarda el numérico para poder compararlos.
        var id = gid.StartsWith(ShopGidPrefix, StringComparison.Ordinal) ? gid[ShopGidPrefix.Length..] : gid;
        if (id.Length == 0 || id.Length > ShopifyInstall.ShopIdMaxLength)
        {
            throw Invalid("shop", "shop_id_unusable");
        }

        return new ShopifyShopInfo(id, ReadString(node, "name") ?? string.Empty, ReadString(node, "myshopifyDomain") ?? shop);
    }

    public async Task<IReadOnlyList<ShopifyWebhookSubscription>> ListWebhooksAsync(string shop, string token, CancellationToken cancellationToken)
    {
        using var document = await GraphQlAsync("webhooks", shop, token, WebhooksQuery, variables: null, cancellationToken);
        if (!TryData(document.RootElement, "webhookSubscriptions", out var connection)
            || !connection.TryGetProperty("edges", out var edges)
            || edges.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("webhooks", "no_subscriptions");
        }

        var subscriptions = new List<ShopifyWebhookSubscription>();
        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind == JsonValueKind.Object
                && edge.TryGetProperty("node", out var node)
                && ReadSubscription(node) is { } subscription)
            {
                subscriptions.Add(subscription);
            }
        }

        return subscriptions;
    }

    public async Task<ShopifyWebhookSubscription> CreateWebhookAsync(
        string shop, string token, string topic, Uri callback, CancellationToken cancellationToken)
    {
        // Quien llama manda el topic en la forma REST (ShopifyWebhookTopics.Registered); GraphQL pide su enum.
        var variables = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["topic"] = ShopifyWebhookTopics.ToGraphQl(topic),
            ["webhookSubscription"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["uri"] = callback.ToString(),
                ["format"] = "JSON",
            },
        };
        using var document = await GraphQlAsync("webhook-create", shop, token, CreateWebhookMutation, variables, cancellationToken);
        if (!TryData(document.RootElement, "webhookSubscriptionCreate", out var payload))
        {
            throw Invalid("webhook-create", "no_payload");
        }

        if (payload.TryGetProperty("userErrors", out var userErrors)
            && userErrors.ValueKind == JsonValueKind.Array
            && userErrors.GetArrayLength() > 0)
        {
            LogInvalid(logger, "webhook-create", "user_errors");
            throw new ShopifyGatewayException(ShopifyGatewayFailure.Rejected, (int)HttpStatusCode.OK);
        }

        return payload.TryGetProperty("webhookSubscription", out var node) && ReadSubscription(node) is { } subscription
            ? subscription
            : throw Invalid("webhook-create", "no_subscription");
    }

    // El dominio arma el host de la URL. El handler ya lo normalizó; esto cierra la puerta a un llamador futuro que no.
    private static string Host(string shop) =>
        ShopifyShopDomain.TryNormalize(shop, out var normalized) && string.Equals(normalized, shop, StringComparison.Ordinal)
            ? normalized
            : throw new ArgumentException("The shop is not a normalized *.myshopify.com domain.", nameof(shop));

    private Task<JsonDocument> GraphQlAsync(
        string operation, string shop, string accessToken, string query, object? variables, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"https://{Host(shop)}/admin/api/{shopifyApp.ApiVersion}/graphql.json", UriKind.Absolute))
        {
            Content = JsonContent.Create(variables is null
                ? new Dictionary<string, object> { ["query"] = query }
                : new Dictionary<string, object> { ["query"] = query, ["variables"] = variables }),
        };
        return SendDisposingAsync(operation, request, accessToken, cancellationToken);
    }

    private async Task<JsonDocument> SendDisposingAsync(
        string operation, HttpRequestMessage request, string accessToken, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await SendAsync(operation, request, accessToken, cancellationToken);
        }
    }

    /// <summary>Envía y devuelve el cuerpo ya parseado, o lanza con la clase de la falla. 401, 402 (tienda congelada) y
    /// 403 son rechazo, como el resto de 4xx; 429 y 5xx, inalcanzable: se puede reintentar más tarde.</summary>
    private async Task<JsonDocument> SendAsync(
        string operation, HttpRequestMessage request, string? accessToken, CancellationToken cancellationToken)
    {
        if (accessToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", accessToken);
        }

        string body;
        int status;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, operation, "timeout");
            throw new ShopifyGatewayException(ShopifyGatewayFailure.Unreachable, null);
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, operation, "network");
            throw new ShopifyGatewayException(ShopifyGatewayFailure.Unreachable, null);
        }

        LogAnswered(logger, operation, status);
        if (status is (int)HttpStatusCode.TooManyRequests or >= 500)
        {
            throw new ShopifyGatewayException(ShopifyGatewayFailure.Unreachable, status);
        }

        if (status is < 200 or >= 300)
        {
            throw new ShopifyGatewayException(ShopifyGatewayFailure.Rejected, status);
        }

        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Invalid(operation, "not_an_object");
            }

            return document;
        }
        catch (JsonException)
        {
            throw Invalid(operation, "unreadable");
        }
    }

    // GraphQL responde 200 con "errors" (permiso, consulta inválida, throttling): sin data no hay nada que usar.
    private static bool TryData(JsonElement root, string field, out JsonElement value)
    {
        value = default;
        return root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(field, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static ShopifyWebhookSubscription? ReadSubscription(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && ReadString(node, "id") is { Length: > 0 } id
        && ReadString(node, "topic") is { Length: > 0 } topic
            ? new ShopifyWebhookSubscription(
                id, topic, Uri.TryCreate(ReadString(node, "uri"), UriKind.Absolute, out var uri) ? uri : null)
            : null;

    private ShopifyGatewayException Invalid(string operation, string reason)
    {
        LogInvalid(logger, operation, reason);
        return new ShopifyGatewayException(ShopifyGatewayFailure.Invalid, (int)HttpStatusCode.OK);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
