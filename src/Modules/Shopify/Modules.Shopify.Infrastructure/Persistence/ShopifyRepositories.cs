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

        if (filter.CancelledAfterInvoice)
        {
            // jsonb @>: el mismo criterio que el contador del resumen (wait_detail ->> 'reason'), para que la lista cierre con él.
            query = query.Where(row => row.WaitDetail != null
                && EF.Functions.JsonContains(row.WaitDetail, ShopifyOrderChanges.InvoicedBeforeCancelDetail));
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

    public async Task<IReadOnlyList<ShopifyOrderMirror>> ListImportedAfterAsync(
        Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        // Los ids por SQL y no por LINQ: Guid no tiene operadores de orden en C#, y el orden que importa es el de uuid de Postgres,
        // el mismo del ORDER BY. Después, las filas por esos ids (a lo sumo `limit`).
        var imported = nameof(ShopifyImportState.Imported);
        var ids = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT id AS "Value" FROM shopify.orders
            WHERE import_state = {imported} AND qep_order_id IS NOT NULL
              AND ({afterId}::uuid IS NULL OR id > {afterId}::uuid)
            ORDER BY id
            LIMIT {limit}
            """).ToListAsync(cancellationToken);
        return await dbContext.Orders.AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountWaitingByConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ImportState == ShopifyImportState.Waiting)
            .GroupBy(row => row.ConnectionId)
            .Select(group => new { ConnectionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ConnectionId, group => group.Count, cancellationToken);

    // El payload es el snapshot normalizado (ShopifyOrderIngestion.WritePayload): los atributos del checkout, vengan de REST
    // (note_attributes) o de GraphQL (customAttributes), quedan en el objeto "attributes". Sólo se miran los pedidos más recientes
    // de la tienda: una tienda vieja tiene miles de filas, y una clave que dejó de usarse hace cientos de pedidos no le sirve al
    // select. El jsonb_typeof protege de un payload sin el objeto (jsonb_object_keys falla con otro tipo).
    public async Task<IReadOnlyList<string>> ListAttributeKeysAsync(
        Guid tenantId, Guid connectionId, int recentOrders, CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQuery<string>(
            $"""
            SELECT DISTINCT keys.key AS "Value"
            FROM (
              SELECT payload -> 'attributes' AS attributes
              FROM shopify.orders
              WHERE tenant_id = {tenantId} AND connection_id = {connectionId}
              ORDER BY shopify_created_at DESC, id DESC
              LIMIT {recentOrders}
            ) recent
            CROSS JOIN LATERAL jsonb_object_keys(
              CASE WHEN jsonb_typeof(recent.attributes) = 'object' THEN recent.attributes ELSE jsonb_build_object() END) AS keys(key)
            """).ToListAsync(cancellationToken);

    // Por IX_shopify_orders_tenant_qep_order. Con tracking: la corrección y la decisión de un cambio escriben la fila.
    public Task<ShopifyOrderMirror?> FindByQepOrderIdAsync(Guid tenantId, Guid qepOrderId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .Where(row => row.TenantId == tenantId && row.QepOrderId == qepOrderId)
            .OrderByDescending(row => row.ShopifyCreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopifyOrderMirror>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return wanted.Length == 0
            ? []
            : await dbContext.Orders.AsNoTracking()
                .Where(row => row.TenantId == tenantId && wanted.Contains(row.Id))
                .ToListAsync(cancellationToken);
    }

    // Spec 2026-11-17 (onboarding), plan D11. Dos lecturas: los conteos por motivo y la muestra de SKU de product_unmatched. El
    // jsonb_typeof protege de un wait_detail sin el arreglo (jsonb_array_elements_text falla con otro tipo).
    public async Task<IReadOnlyList<ShopifyWaitingGroup>> SummarizeWaitingAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<long>? shopifyOrderIds, CancellationToken cancellationToken)
    {
        var waiting = nameof(ShopifyImportState.Waiting);
        var all = shopifyOrderIds is null;
        var ids = shopifyOrderIds?.ToArray() ?? [];
        if (!all && ids.Length == 0)
        {
            return [];
        }

        var counts = await dbContext.Database.SqlQuery<WaitingRow>(
            $"""
            SELECT wait_reason AS "Reason", count(*)::int AS "Count"
            FROM shopify.orders
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND import_state = {waiting}
              AND wait_reason IS NOT NULL
              AND ({all} OR shopify_order_id = ANY({ids}))
            GROUP BY wait_reason
            """).ToListAsync(cancellationToken);
        if (counts.Count == 0)
        {
            return [];
        }

        var unmatched = ShopifyWaitReasons.ProductUnmatched;
        IReadOnlyList<string> sample = counts.Any(row => row.Reason == unmatched)
            ? await dbContext.Database.SqlQuery<string>(
                $"""
                SELECT DISTINCT skus.sku AS "Value"
                FROM shopify.orders
                CROSS JOIN LATERAL jsonb_array_elements_text(
                  CASE WHEN jsonb_typeof(wait_detail -> 'skus') = 'array' THEN wait_detail -> 'skus' ELSE '[]'::jsonb END) AS skus(sku)
                WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND import_state = {waiting}
                  AND wait_reason = {unmatched}
                  AND ({all} OR shopify_order_id = ANY({ids}))
                  AND skus.sku IS NOT NULL AND skus.sku <> ''
                ORDER BY skus.sku
                LIMIT 5
                """).ToListAsync(cancellationToken)
            : [];

        return counts
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Reason, StringComparer.Ordinal)
            .Select(row => new ShopifyWaitingGroup(row.Reason, row.Count, row.Reason == unmatched ? sample : []))
            .ToArray();
    }

    public Task<int> CountImportedSinceAsync(Guid tenantId, Guid connectionId, DateTimeOffset since, CancellationToken cancellationToken) =>
        dbContext.Orders.AsNoTracking().CountAsync(
            row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ImportedAt >= since,
            cancellationToken);

    // El resultado de SqlQuery: EF lo llena por nombre de columna.
    private sealed class WaitingRow
    {
        public string Reason { get; init; } = string.Empty;

        public int Count { get; init; }
    }

    // El resultado de SqlQuery: EF lo llena por nombre de columna.
    private sealed class CountsRow
    {
        public int Waiting { get; init; }

        public int WithWarnings { get; init; }

        public int CancelledAfterInvoice { get; init; }

        public int ImportedToday { get; init; }
    }
}

/// <summary>Spec 2026-11-14 §5: los envíos a Shopify. Con tracking. El pendiente se busca también entre lo agregado y sin guardar:
/// dos encolados en la misma unidad de trabajo se fusionan en vez de chocar con el índice.</summary>
internal sealed class ShopifyOrderPushRepository(ShopifyDbContext dbContext) : IShopifyOrderPushRepository
{
    private static readonly string Held = nameof(ShopifyOrderPushStatus.Held);

    private static readonly string Pending = nameof(ShopifyOrderPushStatus.Pending);

    public async Task<ShopifyOrderPush?> FindOpenAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken) =>
        dbContext.OrderPushes.Local.FirstOrDefault(push => push.TenantId == tenantId && push.MirrorOrderId == mirrorOrderId && push.IsOpen)
        ?? await dbContext.OrderPushes.SingleOrDefaultAsync(
            push => push.TenantId == tenantId
                && push.MirrorOrderId == mirrorOrderId
                && (push.Status == ShopifyOrderPushStatus.Pending || push.Status == ShopifyOrderPushStatus.Held),
            cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountHeldByConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.OrderPushes
            .AsNoTracking()
            .Where(push => push.TenantId == tenantId && push.Status == ShopifyOrderPushStatus.Held)
            .GroupBy(push => push.ConnectionId)
            .Select(group => new { ConnectionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ConnectionId, group => group.Count, cancellationToken);

    public Task<int> ReleaseHeldAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.order_pushes
               SET status = {Pending}, failure_code = NULL, attempts = 0, lease_until = NULL, updated_at = {now}, version = version + 1
             WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND status = {Held}
            """,
            cancellationToken);

    public Task<ShopifyOrderPush?> FindLatestAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken) =>
        dbContext.OrderPushes
            .Where(push => push.TenantId == tenantId && push.MirrorOrderId == mirrorOrderId)
            .OrderByDescending(push => push.CreatedAt)
            .ThenByDescending(push => push.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ShopifyOrderPush?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.OrderPushes.SingleOrDefaultAsync(push => push.Id == id, cancellationToken);

    public void Add(ShopifyOrderPush push) => dbContext.OrderPushes.Add(push);
}

/// <summary>Spec 2026-11-14 §3: los cambios de Shopify. Con tracking para escribir; las listas, sin. Todo filtra por
/// <c>tenant_id</c>.</summary>
internal sealed class ShopifyOrderEditRepository(ShopifyDbContext dbContext) : IShopifyOrderEditRepository
{
    private static readonly ShopifyOrderEditStatus[] ToReview = [ShopifyOrderEditStatus.Pending, ShopifyOrderEditStatus.Blocked];

    // IX_order_edits_mirror_status. El más nuevo, por si una carrera dejó dos abiertos: el siguiente snapshot los reemplaza.
    public Task<ShopifyOrderEdit?> FindOpenAsync(Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken) =>
        dbContext.OrderEdits
            .Where(edit => edit.TenantId == tenantId && edit.MirrorOrderId == mirrorOrderId && ToReview.Contains(edit.Status))
            .OrderByDescending(edit => edit.CreatedAt)
            .ThenByDescending(edit => edit.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ShopifyOrderEdit?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.OrderEdits.SingleOrDefaultAsync(edit => edit.TenantId == tenantId && edit.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ShopifyOrderEdit>> ListByMirrorAsync(
        Guid tenantId, Guid mirrorOrderId, CancellationToken cancellationToken) =>
        await dbContext.OrderEdits
            .AsNoTracking()
            .Where(edit => edit.TenantId == tenantId && edit.MirrorOrderId == mirrorOrderId)
            .OrderByDescending(edit => edit.CreatedAt)
            .ThenByDescending(edit => edit.Id)
            .ToListAsync(cancellationToken);

    // IX_order_edits_tenant_status_created (tenant_id, status, created_at).
    public async Task<(IReadOnlyList<ShopifyOrderEdit> Items, int Total)> SearchAsync(
        Guid tenantId,
        IReadOnlyCollection<ShopifyOrderEditStatus> statuses,
        Guid? connectionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var wanted = statuses.ToArray();
        var query = dbContext.OrderEdits.AsNoTracking().Where(edit => edit.TenantId == tenantId && wanted.Contains(edit.Status));
        if (connectionId is { } connection)
        {
            query = query.Where(edit => edit.ConnectionId == connection);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(edit => edit.CreatedAt)
            .ThenByDescending(edit => edit.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountToReviewAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.OrderEdits.CountAsync(edit => edit.TenantId == tenantId && ToReview.Contains(edit.Status), cancellationToken);

    public void Add(ShopifyOrderEdit edit) => dbContext.OrderEdits.Add(edit);
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
