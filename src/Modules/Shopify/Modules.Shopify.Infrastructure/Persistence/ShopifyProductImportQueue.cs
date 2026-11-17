using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-17 §5.2: la cola de <c>ShopifyProductImportWorker</c>, como la de los envíos a Shopify. El reclamo, la renovación
/// y la falla son SQL que se confirma solo: no pasan por la entidad cargada, así que el guardado de una página no los pisa (EF sólo
/// escribe las columnas que cambiaron en la entidad).
/// </summary>
internal sealed class ShopifyProductImportQueue(ShopifyDbContext dbContext) : IShopifyProductImportQueue
{
    private static readonly string Pending = nameof(ShopifyProductImportStatus.Pending);

    private static readonly string Running = nameof(ShopifyProductImportStatus.Running);

    private static readonly string Failed = nameof(ShopifyProductImportStatus.Failed);

    // SKIP LOCKED: otra réplica que reclama a la vez salta la fila tomada en vez de esperarla. La más vieja primero.
    public async Task<ShopifyProductImportClaim?> TryClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + ShopifyProductImportProcessor.Lease;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            WITH claimable AS (
                SELECT id FROM shopify.product_imports
                 WHERE status IN ({Pending}, {Running}) AND (lease_until IS NULL OR lease_until < {now})
                 ORDER BY created_at, id
                 LIMIT 1
                   FOR UPDATE SKIP LOCKED)
            UPDATE shopify.product_imports job
               SET status = {Running},
                   lease_until = {leaseUntil},
                   attempts = job.attempts + 1
              FROM claimable
             WHERE job.id = claimable.id
            RETURNING job.id AS "ImportId", job.tenant_id AS "TenantId", job.attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? new ShopifyProductImportClaim(rows[0].ImportId, rows[0].TenantId, rows[0].Attempts) : null;
    }

    public Task RenewAsync(Guid importId, DateTimeOffset until, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.product_imports
               SET lease_until = {until}
             WHERE id = {importId} AND status IN ({Pending}, {Running})
            """,
            cancellationToken);

    public Task MarkFailedAsync(Guid importId, string code, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.product_imports
               SET status = {Failed}, failure_code = {code}, lease_until = NULL, finished_at = {now}
             WHERE id = {importId} AND status IN ({Pending}, {Running})
            """,
            cancellationToken);

    private sealed record ClaimRow(Guid ImportId, Guid TenantId, int Attempts);
}

/// <summary><see cref="IShopifyProductSyncScope"/>: un scope nuevo por producto, con su propio <c>ShopifyDbContext</c> y su
/// <see cref="ShopifyProductSync"/>.</summary>
internal sealed class ShopifyProductSyncScope(IServiceScopeFactory scopeFactory) : IShopifyProductSyncScope
{
    public async Task<ShopifyProductOutcome> ApplyAsync(
        ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShopifyProductSync>().ApplyAsync(context, product, cancellationToken);
    }

    public async Task<ShopifyProductOutcome> DeleteAsync(
        ShopifyProductSyncContext context, long shopifyProductId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShopifyProductSync>().DeleteAsync(context, shopifyProductId, cancellationToken);
    }
}
