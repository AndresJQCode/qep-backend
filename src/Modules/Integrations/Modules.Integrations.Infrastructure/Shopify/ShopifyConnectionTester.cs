using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.1, probador de <c>shopify</c>, con el token guardado y sobre <see cref="IShopifyAdminGateway"/>:
/// lee <c>shop { id name myshopifyDomain }</c>, luego las suscripciones de la app, y vuelve a crear los topics de
/// <see cref="ShopifyWebhookTopics.Registered"/> que falten. Un 200 refresca <c>shopName</c> y repone
/// <c>webhookSubscriptionIds</c> (D-S19). Clases:
/// <list type="bullet">
/// <item>la tienda responde 401, 402 (tienda congelada) u otro 4xx → <c>credentials_rejected</c>;</item>
/// <item>red, timeout, 429 o 5xx, o una respuesta ilegible → «no pude verificar» (<c>provider_unreachable</c>);</item>
/// <item>Shopify no acepta un topic → <c>webhooks_not_registered</c>, el mismo código que deja el enlace: la tienda no
/// le manda pedidos a QEP, así que la conexión no está sana aunque el token sirva.</item>
/// </list>
/// Sin <c>WebhookCallbackUrl</c> (local sin túnel) sólo se refresca el nombre.
/// </summary>
internal sealed class ShopifyConnectionTester(IShopifyAdminGateway gateway, IShopifyAppSettings shopifyApp) : IProviderConnectionTester
{
    public string ProviderKey => IntegrationProviders.Shopify.Key;

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        if (!secrets.TryGetValue(ShopifyFieldKeys.AccessToken, out var token) || string.IsNullOrWhiteSpace(token)
            || !fields.TryGetValue(ShopifyFieldKeys.ShopDomain, out var shop)
            || !ShopifyShopDomain.TryNormalize(shop, out var normalized)
            || !string.Equals(normalized, shop, StringComparison.Ordinal))
        {
            return ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected);
        }

        ShopifyShopInfo info;
        try
        {
            info = await gateway.GetShopAsync(shop, token, cancellationToken);
        }
        catch (ShopifyGatewayException exception)
        {
            return Classify(exception.Kind, exception.Status);
        }

        var refreshed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            refreshed[ShopifyFieldKeys.ShopName] = info.Name;
        }

        if (shopifyApp.WebhookCallbackUrl is not { } callback)
        {
            return ConnectionTestResult.OkWith(refreshed);
        }

        var registration = await ShopifyWebhookRegistration.EnsureAsync(gateway, shop, token, callback, cancellationToken);
        if (registration.Failure is { } failure)
        {
            return failure == ShopifyGatewayFailure.Unreachable
                ? ConnectionTestResult.Unreachable("webhooks_unreachable")
                : ConnectionTestResult.RejectedWith(ConnectionFailureCodes.WebhooksNotRegistered);
        }

        refreshed[ShopifyFieldKeys.WebhookSubscriptionIds] = registration.JoinedIds;
        return ConnectionTestResult.OkWith(refreshed);
    }

    // La razón es técnica y nunca lleva un valor: no sale por HTTP.
    private static ConnectionTestResult Classify(ShopifyGatewayFailure kind, int? status) =>
        kind switch
        {
            ShopifyGatewayFailure.Rejected => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected),
            ShopifyGatewayFailure.Invalid => ConnectionTestResult.Unreachable("shop_unreadable"),
            _ => ConnectionTestResult.Unreachable(status is { } code ? $"http_{code}" : "network"),
        };
}
