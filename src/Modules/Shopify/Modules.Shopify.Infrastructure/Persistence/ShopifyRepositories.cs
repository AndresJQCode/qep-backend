using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>IShopifyUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class ShopifyStoreRepository(ShopifyDbContext dbContext) : IShopifyStoreRepository
{
    public Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Stores.SingleOrDefaultAsync(
            store => store.TenantId == tenantId && store.ConnectionId == connectionId, cancellationToken);

    public void Add(ShopifyStore store) => dbContext.Stores.Add(store);

    public async Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
        await dbContext.Stores.AsNoTracking().Where(store => store.ShopDomain == shopDomain).ToListAsync(cancellationToken);
}

internal sealed class ShopifyOrderMirrorRepository(ShopifyDbContext dbContext) : IShopifyOrderMirrorRepository
{
    public Task<ShopifyOrderMirror?> FindAsync(Guid tenantId, Guid connectionId, long shopifyOrderId, CancellationToken cancellationToken) =>
        dbContext.Orders.SingleOrDefaultAsync(
            row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ShopifyOrderId == shopifyOrderId,
            cancellationToken);

    public void Add(ShopifyOrderMirror row) => dbContext.Orders.Add(row);

    // Sin tracking: sólo se lee un id. Acotada por IX_shopify_orders_connection_customer.
    public Task<Guid?> FindSeenCustomerIdAsync(Guid tenantId, Guid connectionId, long shopifyCustomerId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId
                && row.ConnectionId == connectionId
                && row.ShopifyCustomerId == shopifyCustomerId
                && row.QepCustomerId != null)
            .OrderByDescending(row => row.ShopifyCreatedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => row.QepCustomerId)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> DeleteByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(cancellationToken);

    // Sin tracking: la conciliación pasa cada una por la ingesta, que relee la fila con tracking. Acotada por
    // IX_shopify_orders_tenant_state_created (tenant_id, import_state, shopify_created_at).
    public async Task<IReadOnlyList<ShopifyWaitingOrder>> ListWaitingAsync(
        Guid tenantId, Guid connectionId, int limit, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ImportState == ShopifyImportState.Waiting)
            .OrderBy(row => row.ShopifyCreatedAt)
            .ThenBy(row => row.Id)
            .Take(limit)
            .Select(row => new ShopifyWaitingOrder(row.ShopifyOrderId, row.Payload))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// §5.3: el lease y la ventana de la conciliación con <c>UPDATE</c> directos, que se confirman en el acto y no pasan por el tracker
/// (<see cref="IShopifyReconcileLeases"/>): el <c>SaveChanges</c> que el importador hace a mitad de camino no puede confirmar ni
/// pisar un lease. Tomarlo es atómico entre réplicas: el <c>UPDATE</c> condicionado al lease vencido tiene un solo ganador. No
/// sube <c>version</c>.
/// </summary>
internal sealed class ShopifyReconcileLeases(ShopifyDbContext dbContext) : IShopifyReconcileLeases
{
    public async Task<bool> TryAcquireAsync(Guid tenantId, Guid connectionId, TimeSpan duration, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE shopify.stores SET reconcile_lease_until = now() + {duration}
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId}
              AND (reconcile_lease_until IS NULL OR reconcile_lease_until < now())
            """,
            cancellationToken) == 1;

    public Task ReleaseAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE shopify.stores SET reconcile_lease_until = NULL WHERE tenant_id = {tenantId} AND connection_id = {connectionId}",
            cancellationToken);

    public Task RecordReconciledAsync(Guid tenantId, Guid connectionId, DateTimeOffset reconciledAt, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE shopify.stores SET last_reconciled_at = {reconciledAt} WHERE tenant_id = {tenantId} AND connection_id = {connectionId}",
            cancellationToken);
}
