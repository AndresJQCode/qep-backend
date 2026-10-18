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

    // Un solo UPDATE: las conexiones viejas salen de shopify.stores (el dominio queda en la tienda aunque la conexión se borre) y
    // el NOT EXISTS evita chocar con IX_shopify_orders_connection_order si el pedido ya tiene fila en la conexión nueva.
    public Task<int> AdoptWaitingOrdersAsync(Guid tenantId, Guid connectionId, string shopDomain, CancellationToken cancellationToken)
    {
        var previousConnections = dbContext.Stores
            .Where(store => store.TenantId == tenantId && store.ShopDomain == shopDomain && store.ConnectionId != connectionId)
            .Select(store => store.ConnectionId);
        var alreadyThere = dbContext.Orders
            .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId)
            .Select(row => row.ShopifyOrderId);
        return dbContext.Orders
            .Where(row => row.TenantId == tenantId
                && row.ImportState == ShopifyImportState.Waiting
                && previousConnections.Contains(row.ConnectionId)
                && !alreadyThere.Contains(row.ShopifyOrderId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.ConnectionId, connectionId)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
    }

    public async Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
        await dbContext.Stores.AsNoTracking().Where(store => store.ShopDomain == shopDomain).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopifyStore>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Stores.AsNoTracking().Where(store => store.TenantId == tenantId).ToListAsync(cancellationToken);
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

    public Task<ShopifyOrderMirror?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.Orders.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);

    // Por estado y más recientes primero: IX_shopify_orders_tenant_state_created (tenant_id, import_state, shopify_created_at DESC).
    public async Task<(IReadOnlyList<ShopifyOrderMirror> Items, int Total)> SearchAsync(
        Guid tenantId, ShopifyOrderFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var states = filter.States.ToArray();
        var query = dbContext.Orders.AsNoTracking().Where(row => row.TenantId == tenantId);
        if (states.Length > 0)
        {
            query = query.Where(row => states.Contains(row.ImportState));
        }

        if (filter.ConnectionId is { } connectionId)
        {
            query = query.Where(row => row.ConnectionId == connectionId);
        }

        if (filter.WithWarnings)
        {
            // Una fila sin avisos guarda warnings NULL (MarkImported nunca guarda un arreglo vacío).
            query = query.Where(row => row.Warnings != null && row.WarningsReviewedAt == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(row => row.ShopifyCreatedAt)
            .ThenByDescending(row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    // Una sola pasada con FILTER. wait_detail es jsonb: ->> lee la razón de invoiced_before_cancel (ShopifyOrderChanges).
    public async Task<ShopifyOrderCounts> CountAsync(Guid tenantId, DateTimeOffset importedSince, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<CountsRow>(
            $"""
            SELECT
              count(*) FILTER (WHERE import_state = 'Waiting')::int AS "Waiting",
              count(*) FILTER (WHERE warnings IS NOT NULL AND warnings_reviewed_at IS NULL)::int AS "WithWarnings",
              count(*) FILTER (WHERE import_state = 'Cancelled' AND wait_detail ->> 'reason' = 'invoiced_before_cancel')::int AS "CancelledAfterInvoice",
              count(*) FILTER (WHERE imported_at >= {importedSince})::int AS "ImportedToday"
            FROM shopify.orders
            WHERE tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);
        var counts = rows.Single();
        return new ShopifyOrderCounts(counts.Waiting, counts.WithWarnings, counts.CancelledAfterInvoice, counts.ImportedToday);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountWaitingByConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ImportState == ShopifyImportState.Waiting)
            .GroupBy(row => row.ConnectionId)
            .Select(group => new { ConnectionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ConnectionId, group => group.Count, cancellationToken);

    // El resultado de SqlQuery: EF lo llena por nombre de columna.
    private sealed class CountsRow
    {
        public int Waiting { get; init; }

        public int WithWarnings { get; init; }

        public int CancelledAfterInvoice { get; init; }

        public int ImportedToday { get; init; }
    }
}

/// <summary>
/// §5.3: el lease y la ventana de la conciliación con <c>UPDATE</c> directos, que se confirman en el acto y no pasan por el tracker
/// (<see cref="IShopifyReconcileLeases"/>): el <c>SaveChanges</c> que el importador hace a mitad de camino no puede confirmar ni
/// pisar un lease. Tomarlo es atómico entre réplicas: el <c>UPDATE</c> condicionado al lease vencido tiene un solo ganador. No
/// sube <c>version</c>.
/// </summary>
internal sealed class ShopifyReconcileLeases(ShopifyDbContext dbContext) : IShopifyReconcileLeases
{
    // RETURNING: el valor que quedó es el «token» del lease. Sin componer (EF lo mandaría como subconsulta y un UPDATE no lo es).
    public async Task<DateTimeOffset?> TryAcquireAsync(Guid tenantId, Guid connectionId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var taken = await dbContext.Database.SqlQuery<DateTimeOffset>(
            $"""
            UPDATE shopify.stores SET reconcile_lease_until = now() + {duration}
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId}
              AND (reconcile_lease_until IS NULL OR reconcile_lease_until < now())
            RETURNING reconcile_lease_until AS "Value"
            """).ToListAsync(cancellationToken);
        return taken.Count == 1 ? taken[0] : null;
    }

    // Sólo si el lease sigue siendo el que esta corrida tomó. Si venció y otra corrida lo tomó, es suyo.
    public Task ReleaseAsync(Guid tenantId, Guid connectionId, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE shopify.stores SET reconcile_lease_until = NULL
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND reconcile_lease_until = {leaseUntil}
            """,
            cancellationToken);

    public Task RecordReconciledAsync(Guid tenantId, Guid connectionId, DateTimeOffset reconciledAt, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE shopify.stores SET last_reconciled_at = {reconciledAt} WHERE tenant_id = {tenantId} AND connection_id = {connectionId}",
            cancellationToken);
}
