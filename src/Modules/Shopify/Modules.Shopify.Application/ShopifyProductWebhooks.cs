using BuildingBlocks.Domain.Pricing;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §6, plan D1 y D9, P8: un <c>products/create|update|delete</c> ya reclamado por el worker de entregas.
/// <list type="number">
/// <item>Tienda sin <c>products_synced_at</c> → <c>products_not_synced</c>: el comercio todavía no eligió traer sus productos.</item>
/// <item>Conexión no operativa → <c>connection_not_operational</c>.</item>
/// <item>Del cuerpo REST sólo el id y el <c>updated_at</c> (D1). <c>delete</c> desactiva el producto importado sin llamar a Shopify.</item>
/// <item><c>create</c>/<c>update</c>: si el vínculo guardado ya es tan nuevo como el aviso, no se lee Shopify. Si no, se relee el
/// producto por GraphQL (<c>null</c>: ya lo borraron) y se aplica como el sistema (P3).</item>
/// </list>
/// Sin token con la conexión operativa, <c>THROTTLED</c> o Shopify caído: lanza, y la entrega vuelve con su lease. Credencial
/// rechazada → <c>token_expired</c>; scope retirado → <c>scope_missing</c>. Moneda fuera del catálogo, tienda sin empresa o un
/// producto que Catalog rechaza no tienen importación a la que anotarse: la entrega queda procesada, con un log (id de producto y
/// código, nunca el cuerpo) y el contador <c>qep_shopify_product_sync_failures_total{reason}</c>.
/// </summary>
public sealed partial class ShopifyProductWebhooks(
    IShopifyStoreRepository stores,
    IShopifyProductPayloadParser parser,
    IShopifyProductLinkRepository links,
    ShopifyProductPricingResolver pricing,
    IShopifyProductsSource source,
    IShopifyProductSyncScope sync,
    IShopifyProductSyncMetrics metrics,
    ILogger<ShopifyProductWebhooks> logger)
{
    /// <summary>El borrado no usa la regla de precios (sólo desactiva): no hace falta leer la tienda en Shopify para armarla.</summary>
    private static readonly ShopifyPricingContext DeletePricing =
        new("COP", true, false, new PriceRounding(0, PriceRoundingMode.Nearest), new ShopifyDefaultTaxRates(null, null));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify product {ShopifyProductId} of connection {ConnectionId} was not synced: {Code}.")]
    private static partial void LogNotSynced(ILogger logger, long shopifyProductId, Guid connectionId, string code);

    public async Task<ShopifyDeliveryResult> ApplyAsync(
        ShopifyConnectionRef connection, ShopifyWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(delivery);

        var store = await stores.FindAsync(connection.TenantId, connection.ConnectionId, cancellationToken);
        if (store?.ProductsSyncedAt is null)
        {
            return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.ProductsNotSynced);
        }

        if (!connection.IsOperational)
        {
            return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.ConnectionNotOperational);
        }

        var reference = parser.ParseRef(delivery.Payload);
        if (string.Equals(delivery.Topic, ShopifyWebhookTopics.ProductsDelete, StringComparison.Ordinal))
        {
            var deleted = await sync.DeleteAsync(
                new ShopifyProductSyncContext(connection, DeletePricing, Guid.Empty, ShopifySyncTrigger.Webhook), reference.Id, cancellationToken);
            Report(connection, reference.Id, deleted);
            return ShopifyDeliveryResult.Processed;
        }

        if (reference.UpdatedAt is { } updatedAt
            && (await links.FindByShopifyProductAsync(connection.ConnectionId, reference.Id, cancellationToken))
                .Select(link => link.ShopifyUpdatedAt)
                .Max() is { } applied
            && applied >= updatedAt)
        {
            // §6, orden: un aviso que no es más nuevo que lo aplicado no vuelve a leer Shopify.
            return ShopifyDeliveryResult.Processed;
        }

        if (connection.AccessToken is null)
        {
            // Spec 2026-11-12 §6.4: el refresco del token va tarde; la entrega vuelve con su lease y no se pierde.
            throw new InvalidOperationException("The Shopify access token is waiting for its refresh; the product delivery is retried.");
        }

        try
        {
            var resolution = await pricing.ResolveAsync(connection, cancellationToken);
            if (resolution.FailureCode is { } code)
            {
                Fail(connection, reference.Id, code);
                return ShopifyDeliveryResult.Processed;
            }

            var product = await source.ReadProductAsync(connection, reference.Id, cancellationToken);
            if (product is null)
            {
                return ShopifyDeliveryResult.Processed;
            }

            var outcome = await sync.ApplyAsync(
                new ShopifyProductSyncContext(connection, resolution.Pricing!, Guid.Empty, ShopifySyncTrigger.Webhook), product, cancellationToken);
            Report(connection, reference.Id, outcome);
            return ShopifyDeliveryResult.Processed;
        }
        catch (ShopifyProductsSourceException exception) when (exception.Failure == ShopifyProductsSourceFailure.Rejected)
        {
            return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.TokenExpired);
        }
        catch (ShopifyProductsSourceException exception) when (exception.Failure == ShopifyProductsSourceFailure.AccessDenied)
        {
            return ShopifyDeliveryResult.Ignored(ShopifyIgnoredReasons.ScopeMissing);
        }
    }

    private void Report(ShopifyConnectionRef connection, long shopifyProductId, ShopifyProductOutcome outcome)
    {
        if (outcome.Kind != ShopifyProductOutcomeKind.Skipped)
        {
            return;
        }

        foreach (var code in outcome.IssueCodes)
        {
            Fail(connection, shopifyProductId, code);
        }
    }

    private void Fail(ShopifyConnectionRef connection, long shopifyProductId, string code)
    {
        LogNotSynced(logger, shopifyProductId, connection.ConnectionId, code);
        metrics.RecordFailure(code);
    }
}
