using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-15 §5.1 y §5.2: <c>shopify.webhook_deliveries</c> por SQL. El único por <c>webhook_id</c> hace que un reintento de
/// Shopify no cree otra fila; el cuerpo ya pasó la firma y va como jsonb tal cual. El reclamo es la regla de
/// <c>WebhookDeliveryWorker</c> de Messaging (H15): una sentencia que se commitea sola y da un solo ganador entre réplicas.
/// </summary>
internal sealed class WebhookDeliveries(ShopifyDbContext dbContext) : IShopifyWebhookDeliveries, IShopifyWebhookDeliveryQueue
{
    // Un solo arreglo para el parámetro del reclamo (CA1861).
    private static readonly TimeSpan[] LeaseArray = [.. ShopifyWebhookDeliveryWorker.Leases];

    public async Task<bool> EnqueueAsync(ShopifyWebhookEnvelope envelope, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = Encoding.UTF8.GetString(envelope.Body);
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO shopify.webhook_deliveries (webhook_id, topic, shop_domain, api_version, payload, received_at, attempts)
            VALUES ({envelope.WebhookId}, {envelope.Topic}, {envelope.ShopDomain}, {envelope.ApiVersion}, {payload}::jsonb, {receivedAt}, 0)
            ON CONFLICT (webhook_id) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }

    public Task<int> DeleteByShopDomainAsync(string shopDomain, long exceptDeliveryId, CancellationToken cancellationToken) =>
        dbContext.Deliveries
            .Where(delivery => delivery.ShopDomain == shopDomain && delivery.Id != exceptDeliveryId)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyList<long>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.Deliveries.AsNoTracking()
            .Where(delivery => delivery.ProcessedAt == null && (delivery.LeaseUntil == null || delivery.LeaseUntil < now))
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => delivery.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<ShopifyWebhookClaim?> TryClaimAsync(long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = LeaseArray;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE shopify.webhook_deliveries
               SET lease_until = {now} + ({leases})[LEAST(attempts + 1, cardinality({leases}))],
                   attempts = attempts + 1
             WHERE id = {id} AND processed_at IS NULL AND (lease_until IS NULL OR lease_until < {now})
            RETURNING attempts AS "Attempts", topic AS "Topic", shop_domain AS "ShopDomain", payload::text AS "Payload"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1
            ? new ShopifyWebhookClaim(new ShopifyWebhookDelivery(id, rows[0].Topic, rows[0].ShopDomain, rows[0].Payload), rows[0].Attempts)
            : null;
    }

    public Task MarkProcessedAsync(
        long id, DateTimeOffset now, string? ignoredReason, string? lastError, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.webhook_deliveries
               SET processed_at = {now}, ignored_reason = {ignoredReason}, last_error = {lastError}
             WHERE id = {id}
            """,
            cancellationToken);

    private sealed record ClaimRow(int Attempts, string Topic, string ShopDomain, string Payload);
}
