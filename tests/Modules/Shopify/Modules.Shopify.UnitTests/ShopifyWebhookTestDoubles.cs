using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.UnitTests;

/// <summary>Los dobles del procesador de entregas: una «base» en memoria con la semántica que importa (lo agregado se ve al
/// buscar, <c>DiscardPendingChanges</c> descarta lo no guardado) y puertos que anotan lo que se les pidió.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class FakeConnectionDirectory : IShopifyConnectionDirectory
{
    public Dictionary<string, ShopifyConnectionRef> Connections { get; } = new(StringComparer.Ordinal);

    public Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.GetValueOrDefault(shopDomain));

    public Task<IReadOnlyList<ShopifyConnectionRef>> ListOperationalAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ShopifyConnectionRef?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.Values.FirstOrDefault(connection => connection.TenantId == tenantId && connection.ConnectionId == connectionId));
}

internal sealed class FakeConnectionHealth : IShopifyConnectionHealth
{
    public List<(Guid TenantId, Guid ConnectionId)> Uninstalled { get; } = [];

    public List<(Guid TenantId, Guid ConnectionId)> Rejected { get; } = [];

    public Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        Uninstalled.Add((tenantId, connectionId));
        return Task.CompletedTask;
    }

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        Rejected.Add((tenantId, connectionId));
        return Task.CompletedTask;
    }
}

internal sealed class FakeOrderImporter : IShopifyOrderImporter
{
    public List<(ShopifyStore Store, ShopifyOrderMirror Row, ShopifyOrderSnapshot Snapshot)> Calls { get; } = [];

    public List<ShopifyConnectionRef> Connections { get; } = [];

    public Task<ShopifyImportOutcome> ImportAsync(
        ShopifyConnectionRef connection, ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        Connections.Add(connection);
        Calls.Add((store, row, snapshot));
        return Task.FromResult(new ShopifyImportOutcome(row.ImportState));
    }
}

/// <summary>La reacción de §6.3 vista desde la ingesta: anota cada cambio y cuántos guardados había cuando llegó, y
/// puede fallar las primeras <see cref="FailuresLeft"/> veces.</summary>
internal sealed class FakeOrderChangeReaction(FakeUnitOfWork unitOfWork) : IShopifyOrderChangeReaction
{
    public List<(ShopifyOrderChange Change, ShopifyMirrorState Current, int SavesSoFar)> Calls { get; } = [];

    public int FailuresLeft { get; set; }

    public Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken)
    {
        Calls.Add((change, ShopifyMirrorState.Of(change.Row), unitOfWork.Saves));
        if (FailuresLeft > 0)
        {
            FailuresLeft--;
            throw new InvalidOperationException("Quotations is down.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Las tablas de la «base»: lo que <see cref="FakeUnitOfWork"/> confirma pasa de pendiente a guardado.</summary>
internal sealed class FakeShopifyDatabase
{
    public Dictionary<Guid, ShopifyStore> Stores { get; } = [];

    public List<ShopifyStore> PendingStores { get; } = [];

    public List<(Guid TenantId, Guid ConnectionId, string ShopDomain)> Adoptions { get; } = [];

    public Dictionary<(Guid ConnectionId, long OrderId), ShopifyOrderMirror> Orders { get; } = [];

    public List<ShopifyOrderMirror> PendingOrders { get; } = [];

    public List<(Guid TenantId, Guid ConnectionId)> DeletedOrdersOf { get; } = [];

    public List<(string ShopDomain, long ExceptDeliveryId)> DeletedDeliveriesOf { get; } = [];

    public List<(string Action, Guid TenantId, Guid ActorId, AuditActorType ActorType, string ResourceId, string Fields)> PendingAudit { get; } = [];

    public List<(string Action, Guid TenantId, Guid ActorId, AuditActorType ActorType, string ResourceId, string Fields)> Audit { get; } = [];

    public List<ShopifyCityCorrection> PendingCityCorrections { get; } = [];

    public List<ShopifyCityCorrection> CityCorrections { get; } = [];

    public List<ShopifyCityCorrectionJob> PendingCityCorrectionJobs { get; } = [];

    public List<ShopifyCityCorrectionJob> CityCorrectionJobs { get; } = [];

    /// <summary>Los trabajos que otra réplica ya terminó: <c>LockIfPendingAsync</c> los ve no pendientes.</summary>
    public HashSet<Guid> CityCorrectionJobsFinishedElsewhere { get; } = [];

    /// <summary>El orden de lo que pasó en la «base» (transacción, candados, guardados), para las pruebas que lo exigen.</summary>
    public List<string> Log { get; } = [];
}

internal sealed class FakeStoreRepository(FakeShopifyDatabase database) : IShopifyStoreRepository
{
    public Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(
            database.Stores.TryGetValue(connectionId, out var store) && store.TenantId == tenantId
                ? store
                : database.PendingStores.FirstOrDefault(pending => pending.ConnectionId == connectionId && pending.TenantId == tenantId));

    public void Add(ShopifyStore store) => database.PendingStores.Add(store);

    /// <summary>Las reasignaciones pedidas; la de verdad (un <c>UPDATE</c>) la prueba la integración.</summary>
    public List<(Guid TenantId, Guid ConnectionId, string ShopDomain)> Adoptions => database.Adoptions;

    public Task<int> AdoptWaitingOrdersAsync(Guid tenantId, Guid connectionId, string shopDomain, CancellationToken cancellationToken)
    {
        database.Adoptions.Add((tenantId, connectionId, shopDomain));
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifyStore>>(
            [.. database.Stores.Values.Where(store => string.Equals(store.ShopDomain, shopDomain, StringComparison.Ordinal))]);

    public Task<IReadOnlyList<ShopifyStore>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifyStore>>([.. database.Stores.Values.Where(store => store.TenantId == tenantId)]);
}

internal sealed class FakeOrderMirrorRepository(FakeShopifyDatabase database) : IShopifyOrderMirrorRepository
{
    public Task<ShopifyOrderMirror?> FindAsync(Guid tenantId, Guid connectionId, long shopifyOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(
            database.Orders.TryGetValue((connectionId, shopifyOrderId), out var row) && row.TenantId == tenantId
                ? row
                : database.PendingOrders.FirstOrDefault(pending =>
                    pending.ConnectionId == connectionId && pending.ShopifyOrderId == shopifyOrderId && pending.TenantId == tenantId));

    public void Add(ShopifyOrderMirror row) => database.PendingOrders.Add(row);

    public Task<Guid?> FindSeenCustomerIdAsync(Guid tenantId, Guid connectionId, long shopifyCustomerId, CancellationToken cancellationToken) =>
        Task.FromResult(
            database.Orders.Values
                .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ShopifyCustomerId == shopifyCustomerId)
                .Where(row => row.QepCustomerId is not null)
                .OrderByDescending(row => row.ShopifyCreatedAt)
                .Select(row => row.QepCustomerId)
                .FirstOrDefault());

    public Task<int> DeleteByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        database.DeletedOrdersOf.Add((tenantId, connectionId));
        var keys = database.Orders.Where(pair => pair.Value.TenantId == tenantId && pair.Key.ConnectionId == connectionId)
            .Select(pair => pair.Key)
            .ToList();
        keys.ForEach(key => database.Orders.Remove(key));
        return Task.FromResult(keys.Count);
    }

    public Task<IReadOnlyList<ShopifyWaitingOrder>> ListWaitingAsync(Guid tenantId, Guid connectionId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifyWaitingOrder>>(
            [.. database.Orders.Values
                .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ImportState == ShopifyImportState.Waiting)
                .OrderBy(row => row.ShopifyCreatedAt)
                .ThenBy(row => row.Id)
                .Take(limit)
                .Select(row => new ShopifyWaitingOrder(row.ShopifyOrderId, row.Payload))]);

    public Task<ShopifyOrderMirror?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(
            database.Orders.Values.FirstOrDefault(row => row.TenantId == tenantId && row.Id == id)
            ?? database.PendingOrders.FirstOrDefault(row => row.TenantId == tenantId && row.Id == id));

    // Los filtros y los conteos son SQL: se prueban contra Postgres (ShopifyInboxApiTests), no acá.
    public Task<(IReadOnlyList<ShopifyOrderMirror> Items, int Total)> SearchAsync(
        Guid tenantId, ShopifyOrderFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>El comienzo de «hoy» con que el resumen pidió los contadores; los conteos en sí son SQL.</summary>
    public DateTimeOffset? LastImportedSince { get; private set; }

    public Task<ShopifyOrderCounts> CountAsync(Guid tenantId, DateTimeOffset importedSince, CancellationToken cancellationToken)
    {
        LastImportedSince = importedSince;
        return Task.FromResult(new ShopifyOrderCounts(0, 0, 0, 0));
    }

    public Task<IReadOnlyDictionary<Guid, int>> CountWaitingByConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeWebhookDeliveries(FakeShopifyDatabase database) : IShopifyWebhookDeliveries
{
    public Task<bool> EnqueueAsync(ShopifyWebhookEnvelope envelope, DateTimeOffset receivedAt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The processor never enqueues.");

    public Task<int> DeleteByShopDomainAsync(string shopDomain, long exceptDeliveryId, CancellationToken cancellationToken)
    {
        database.DeletedDeliveriesOf.Add((shopDomain, exceptDeliveryId));
        return Task.FromResult(1);
    }
}

internal sealed class FakeAuditRecorder(FakeShopifyDatabase database) : IShopifyAuditRecorder
{
    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string changedFieldsJson,
        DateTimeOffset occurredAt) =>
        database.PendingAudit.Add((action, tenantId, actorId, actorType, resourceId, changedFieldsJson));
}

/// <summary><see cref="BeforeSave"/> deja simular a otro escritor: puede cambiar la «base» y lanzar el conflicto que traduciría
/// <c>ShopifyUnitOfWork</c>.</summary>
internal sealed class FakeUnitOfWork(FakeShopifyDatabase database) : IShopifyUnitOfWork
{
    public int Saves { get; private set; }

    public int Discards { get; private set; }

    public Func<int, Task>? BeforeSave { get; set; }

    public List<Guid> SynonymLocks { get; } = [];

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        database.Log.Add("save");
        if (BeforeSave is { } hook)
        {
            await hook(Saves);
        }

        var written = database.PendingStores.Count + database.PendingOrders.Count + database.PendingAudit.Count
            + database.PendingCityCorrections.Count + database.PendingCityCorrectionJobs.Count;
        database.PendingStores.ForEach(store => database.Stores[store.ConnectionId] = store);
        database.PendingOrders.ForEach(row => database.Orders[(row.ConnectionId, row.ShopifyOrderId)] = row);
        database.Audit.AddRange(database.PendingAudit);
        database.CityCorrections.AddRange(database.PendingCityCorrections);
        database.CityCorrectionJobs.AddRange(database.PendingCityCorrectionJobs);
        database.PendingStores.Clear();
        database.PendingOrders.Clear();
        database.PendingAudit.Clear();
        database.PendingCityCorrections.Clear();
        database.PendingCityCorrectionJobs.Clear();
        return written;
    }

    /// <summary>La «base» confirma en cada guardado; la transacción sólo deja su rastro en el registro.</summary>
    public Task<IShopifyTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        database.Log.Add("begin");
        return Task.FromResult<IShopifyTransaction>(new FakeTransaction(database));
    }

    public Task LockCitySynonymAsync(Guid synonymId, CancellationToken cancellationToken)
    {
        SynonymLocks.Add(synonymId);
        database.Log.Add($"synonym-lock:{synonymId}");
        return Task.CompletedTask;
    }

    public void DiscardPendingChanges()
    {
        Discards++;
        database.PendingStores.Clear();
        database.PendingOrders.Clear();
        database.PendingAudit.Clear();
        database.PendingCityCorrections.Clear();
        database.PendingCityCorrectionJobs.Clear();
    }
}

internal sealed class FakeTransaction(FakeShopifyDatabase database) : IShopifyTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken)
    {
        database.Log.Add("commit");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>La cola del worker en memoria: <see cref="Processed"/> es lo que el worker cerró y con qué.</summary>
internal sealed class FakeDeliveryQueue : IShopifyWebhookDeliveryQueue
{
    public Dictionary<long, (ShopifyWebhookDelivery Delivery, int Attempts, DateTimeOffset? LeaseUntil)> Pending { get; } = [];

    public Dictionary<long, (string? IgnoredReason, string? LastError, int Attempts)> Processed { get; } = [];

    public void Enqueue(ShopifyWebhookDelivery delivery) => Pending[delivery.Id] = (delivery, 0, null);

    public Task<IReadOnlyList<long>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<long>>(
            [.. Pending.Where(pair => pair.Value.LeaseUntil is null || pair.Value.LeaseUntil < now).Select(pair => pair.Key).Take(limit)]);

    public Task<ShopifyWebhookClaim?> TryClaimAsync(long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Pending.TryGetValue(id, out var entry) || (entry.LeaseUntil is { } lease && lease >= now))
        {
            return Task.FromResult<ShopifyWebhookClaim?>(null);
        }

        var attempts = entry.Attempts + 1;
        Pending[id] = (entry.Delivery, attempts, now + ShopifyWebhookDeliveryWorker.LeaseFor(attempts));
        return Task.FromResult<ShopifyWebhookClaim?>(new ShopifyWebhookClaim(entry.Delivery, attempts));
    }

    public Task MarkProcessedAsync(long id, DateTimeOffset now, string? ignoredReason, string? lastError, CancellationToken cancellationToken)
    {
        var attempts = Pending[id].Attempts;
        Pending.Remove(id);
        Processed[id] = (ignoredReason, lastError, attempts);
        return Task.CompletedTask;
    }
}
