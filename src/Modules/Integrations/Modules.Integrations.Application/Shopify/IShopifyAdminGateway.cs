namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.3 (canje y enlace) y §4.1 (probador) contra Shopify. El adaptador vive en Infrastructure sobre el
/// cliente nombrado <c>integrations.shopify-admin</c>. A diferencia de <see cref="IWhatsAppSignupGateway"/>, una falla del
/// proveedor <b>lanza</b> <see cref="ShopifyGatewayException"/> con su clase; el handler la traduce a su código. Nunca
/// registra el <c>code</c>, el token ni el client secret.
/// </summary>
public interface IShopifyAdminGateway
{
    /// <summary><c>POST https://{shop}/admin/oauth/access_token { client_id, client_secret, code }</c> → token offline.</summary>
    Task<ShopifyTokenGrant> ExchangeCodeAsync(string shop, string code, CancellationToken cancellationToken);

    /// <summary><c>{ shop { id name myshopifyDomain } }</c> por GraphQL, con <c>X-Shopify-Access-Token</c>.</summary>
    Task<ShopifyShopInfo> GetShopAsync(string shop, string accessToken, CancellationToken cancellationToken);

    /// <summary>Las suscripciones de la app en esa tienda (<c>webhookSubscriptions</c>), para no duplicar un topic.</summary>
    Task<IReadOnlyList<ShopifyWebhookSubscription>> ListWebhooksAsync(string shop, string token, CancellationToken cancellationToken);

    /// <summary><c>webhookSubscriptionCreate</c> con formato JSON; los <c>userErrors</c> son <see cref="ShopifyGatewayFailure.Rejected"/>.</summary>
    Task<ShopifyWebhookSubscription> CreateWebhookAsync(
        string shop, string token, string topic, Uri callback, CancellationToken cancellationToken);
}

/// <param name="Scopes">Los scopes concedidos, separados por coma, tal como los devuelve Shopify.</param>
public sealed record ShopifyTokenGrant(string AccessToken, string Scopes)
{
    // El ToString de un record imprime el token en cualquier log o mensaje que lo interpole.
    public override string ToString() => $"ShopifyTokenGrant {{ Scopes = {Scopes} }}";
}

/// <param name="Id">El id numérico de la tienda (el final de <c>gid://shopify/Shop/{id}</c>), como lo traen los webhooks.</param>
public sealed record ShopifyShopInfo(string Id, string Name, string Domain);

/// <param name="Topic">El enum de GraphQL (<c>ORDERS_CREATE</c>), no el de REST (<c>orders/create</c>).</param>
public sealed record ShopifyWebhookSubscription(string Id, string Topic, Uri? CallbackUrl);

/// <summary>Las tres clases de falla que el handler distingue: Shopify dijo que no (4xx, <c>userErrors</c>), no se pudo
/// hablar con Shopify (red, timeout, 429, 5xx) o respondió algo que no se puede usar (cuerpo ilegible, valor que no
/// cabe en su columna).</summary>
public enum ShopifyGatewayFailure
{
    Rejected,
    Unreachable,
    Invalid,
}

/// <summary>Sin el cuerpo de la respuesta ni nada de lo enviado: Shopify puede repetir el <c>code</c> en un error.</summary>
public sealed class ShopifyGatewayException : Exception
{
    public ShopifyGatewayException(ShopifyGatewayFailure kind, int? status)
        : base($"Shopify call failed: {kind} (HTTP {status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}).")
    {
        Kind = kind;
        Status = status;
    }

    public ShopifyGatewayFailure Kind { get; }

    /// <summary>El status HTTP; <c>null</c> si no hubo respuesta (red o timeout).</summary>
    public int? Status { get; }
}
