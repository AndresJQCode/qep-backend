using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>La entrega reclamada y el número de intento que este reclamo consumió.</summary>
internal sealed record ShopifyWebhookClaim(ShopifyWebhookDelivery Delivery, int Attempts);

/// <summary>Lo que el worker necesita de <c>shopify.webhook_deliveries</c>: separado para probar el worker sin Postgres.</summary>
internal interface IShopifyWebhookDeliveryQueue
{
    Task<IReadOnlyList<long>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<ShopifyWebhookClaim?> TryClaimAsync(long id, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkProcessedAsync(long id, DateTimeOffset now, string? ignoredReason, string? lastError, CancellationToken cancellationToken);
}
