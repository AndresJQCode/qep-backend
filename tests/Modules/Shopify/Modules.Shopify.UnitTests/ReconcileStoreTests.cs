using BuildingBlocks.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §5.3 y §6.4: la conciliación de una tienda. Trae los pedidos actualizados desde <c>last_reconciled_at − 5
/// min</c>, los pasa uno por uno por la misma ingesta del webhook (<see cref="ShopifyOrderIngestion"/>), reintenta los
/// <c>Waiting</c> y, sólo si todo salió, mueve la ventana al instante en que <b>empezó</b> la corrida. Shopify entra como doble
/// (<see cref="FakeOrdersSource"/>) y la espera del <c>THROTTLED</c> no duerme (<see cref="FakeTimeProvider"/>).
/// </summary>
public sealed class ReconcileStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastReconciled = Now.AddMinutes(-10);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReconcileUsesLastReconciledMinusFiveMinutes()
    {
        var world = new World().WithStore(LastReconciled);

        await world.Reconcile.RunAsync(world.Connection, Ct);

        var call = Assert.Single(world.Source.Calls);
        Assert.Equal(LastReconciled.AddMinutes(-5), call.Since);
        Assert.Null(call.Cursor);
        Assert.Same(world.Connection, call.Connection);
    }

    // D-S4: una tienda que la conciliación ve por primera vez nace con la ventana en «ahora», sin historial.
    [Fact]
    public async Task ReconcileStoreSeenForTheFirstTimeStartsFromNowWithoutHistory()
    {
        var world = new World();

        await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal(Now.AddMinutes(-5), Assert.Single(world.Source.Calls).Since);
        var store = Assert.Single(world.Database.Stores.Values);
        Assert.Equal((TenantId, "mi-tienda.myshopify.com"), (store.TenantId, store.ShopDomain));
    }

    // D-S4 y §5.3 (ruling S25): la tienda que se ve por primera vez arranca su ventana en el enlace, no en «ahora». Un pedido
    // hecho entre el enlace y el primer contacto, cuyo webhook se perdió, entra igual.
    [Fact]
    public async Task ReconcileStoreSeenForTheFirstTimeStartsFromTheLink()
    {
        var world = new World();
        var linked = world.Connection with { LinkedAt = Now.AddMinutes(-40) };
        world.Source.FilterBySince = true;
        world.Source.Catalog.Add(Order(55, updatedAt: Now.AddMinutes(-30)));

        await world.Reconcile.RunAsync(linked, Ct);

        Assert.Equal(Now.AddMinutes(-45), Assert.Single(world.Source.Calls).Since);
        Assert.Contains((ConnectionId, 55L), world.Database.Orders.Keys);
    }

    // El primer contacto también puede ser un webhook: la tienda que crea la ingesta nace con la ventana en el enlace.
    [Fact]
    public async Task IngestionFirstContactStartsTheWindowAtTheLink()
    {
        var world = new World();
        var linked = world.Connection with { LinkedAt = Now.AddMinutes(-40) };

        await world.Ingestion.ApplyAsync(linked, Order(1), Ct);

        Assert.Equal(Now.AddMinutes(-40), Assert.Single(world.Database.Stores.Values).LastReconciledAt);
    }

    // Ruling S24: un fallo permanente (una cabecera que el dominio no guarda) es como en el webhook: la fila queda con last_error,
    // el pedido se cuenta como saltado y la ventana avanza. Si la congelara, el mismo pedido la clavaría para siempre.
    [Fact]
    public async Task ReconcilePermanentFailureRecordsLastErrorAndMovesTheWindow()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(1, Now.AddDays(-1));
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1, currency: "PESOS"), Order(2)], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.StartsWith("invalid_payload", world.Database.Orders[(ConnectionId, 1L)].LastError, StringComparison.Ordinal);
        Assert.Contains((ConnectionId, 2L), world.Database.Orders.Keys);
        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal((2, 0, 1), (result.Fetched, result.Failed, result.Skipped));
    }

    [Fact]
    public async Task ReconcilePermanentFailureOfANewOrderIsCountedAndMovesTheWindow()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1, currency: "PESOS")], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Empty(world.Database.Orders);
        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal((0, 1), (result.Failed, result.Skipped));
        // Ronda 2: sin fila del espejo, el único rastro es el log, con tenant, conexión, pedido y motivo; nada del comprador.
        var warning = Assert.Single(world.Logs.Warnings);
        Assert.Contains(TenantId.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains(ConnectionId.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains("order 1 ", warning, StringComparison.Ordinal);
        Assert.Contains("invalid_payload", warning, StringComparison.Ordinal);
        AssertNoBuyerData(warning);
    }

    // El pedido que el cliente no supo leer no desaparece: si la fila existe queda con su last_error, y se cuenta.
    [Fact]
    public async Task ReconcileOrderTheSourceCouldNotReadIsRecordedAndCounted()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(3, Now.AddDays(-1));
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(2)], null) { Skipped = [new ShopifySkippedOrder(3, "invalid_payload: updatedAt")] });

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal("invalid_payload: updatedAt", world.Database.Orders[(ConnectionId, 3L)].LastError);
        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal((1, 0, 1), (result.Fetched, result.Failed, result.Skipped));
        var warning = Assert.Single(world.Logs.Warnings);
        Assert.Contains("order 3", warning, StringComparison.Ordinal);
        AssertNoBuyerData(warning);
    }

    // Ronda 2, minor: un pedido que el cliente no supo leer y cuyo GID no es numérico también se cuenta y deja su log.
    [Fact]
    public async Task ReconcileUnreadableOrderWithoutANumericIdIsCountedAndLogged()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([], null) { Skipped = [new ShopifySkippedOrder(null, "invalid_payload: id: The Shopify order payload has no 'id'.")] });

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal(1, result.Skipped);
        Assert.Equal([Now], world.Leases.Reconciled);
        var warning = Assert.Single(world.Logs.Warnings);
        Assert.Contains("order unknown", warning, StringComparison.Ordinal);
    }

    private static void AssertNoBuyerData(string text)
    {
        foreach (var buyer in new[] { "Laura", "Gómez", "laura@verde.co", "+573001234567", "portería" })
        {
            Assert.DoesNotContain(buyer, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReconcileUnreadableWaitingPayloadIsRecordedAndMovesTheWindow()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(4, Now.AddDays(-1), payload: "{");

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.StartsWith("invalid_payload", world.Database.Orders[(ConnectionId, 4L)].LastError, StringComparison.Ordinal);
        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal((0, 1), (result.Failed, result.Skipped));
    }

    [Fact]
    public async Task ReconcilePaginates()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1), Order(2)], "cursor-1"));
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(3)], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal([null, "cursor-1"], world.Source.Calls.Select(call => call.Cursor));
        Assert.All(world.Source.Calls, call => Assert.Equal(LastReconciled.AddMinutes(-5), call.Since));
        Assert.Equal([1L, 2L, 3L], world.Database.Orders.Keys.Select(key => key.OrderId).Order());
        Assert.Equal(3, result.Fetched);
    }

    [Fact]
    public async Task ReconcileThrottledWaitsRetryAfterThenGivesUpAfterThree()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Throttled, TimeSpan.FromSeconds(7)));
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Throttled, TimeSpan.FromSeconds(7)));
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Throttled, TimeSpan.FromSeconds(7)));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => world.Reconcile.RunAsync(world.Connection, Ct));

        Assert.Equal(ShopifyErrorCodes.StoreSyncUnavailable, error.Code);
        Assert.Equal(3, world.Source.Calls.Count);
        // Tres seguidos: espera después del primero y del segundo; tras el tercero, la tienda queda para la próxima corrida.
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], world.Time.Delays);
        Assert.Empty(world.Leases.Reconciled);
        Assert.Equal(1, world.Leases.Released);
    }

    [Fact]
    public async Task ReconcileThrottledWithoutRetryAfterWaitsTwoSecondsAndGoesOn()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Throttled, null));
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Throttled, null));
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1)], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], world.Time.Delays);
        Assert.Equal(1, result.Fetched);
        Assert.Equal([Now], world.Leases.Reconciled);
    }

    [Fact]
    public async Task Reconcile401ReportsRejectedAndStops()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(9, Now.AddDays(-1));
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Rejected, null));

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Reconcile.RunAsync(world.Connection, Ct));

        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, error.Code);
        Assert.Equal([(TenantId, ConnectionId)], world.Health.Rejected);
        Assert.Single(world.Source.Calls);
        // Sin la conexión no se reintenta nada ni se mueve la ventana.
        Assert.Empty(world.Importer.Calls);
        Assert.Empty(world.Leases.Reconciled);
        Assert.Equal(1, world.Leases.Released);
    }

    [Fact]
    public async Task ReconcileUnreachableShopifyKeepsTheWindow()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Failures.Enqueue(new ShopifyOrdersSourceException(ShopifyOrdersSourceFailure.Unavailable, null));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => world.Reconcile.RunAsync(world.Connection, Ct));

        Assert.Equal(ShopifyErrorCodes.StoreSyncUnavailable, error.Code);
        Assert.Empty(world.Health.Rejected);
        Assert.Empty(world.Leases.Reconciled);
        Assert.Equal(1, world.Leases.Released);
    }

    [Fact]
    public async Task ReconcileRetriesWaitingOldestFirstCap200()
    {
        var world = new World().WithStore(LastReconciled);
        for (var id = 1L; id <= 205; id++)
        {
            // El id más alto es el más viejo: el orden sale de la fecha del pedido, no del id.
            world.WithWaiting(id, Now.AddDays(-1).AddMinutes(-id));
        }

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal(200, world.Importer.Calls.Count);
        Assert.Equal(Enumerable.Range(6, 200).Select(id => (long)id).Reverse(), world.Importer.Calls.Select(call => call.Row.ShopifyOrderId));
        Assert.Equal(0, result.Fetched);
        Assert.Equal(200, result.Waiting);
    }

    // Un pedido que la corrida acaba de traer ya tuvo su intento: no se reintenta otra vez en la misma corrida.
    [Fact]
    public async Task ReconcileDoesNotRetryTwiceAnOrderItJustFetched()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(1, Now.AddDays(-1)).WithWaiting(2, Now.AddDays(-2));
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1, updatedAt: Now.AddMinutes(-1))], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal([1L, 2L], world.Importer.Calls.Select(call => call.Row.ShopifyOrderId));
        Assert.Equal((1, 2), (result.Fetched, result.Waiting));
    }

    [Fact]
    public async Task ReconcileLeaseHeldSkips()
    {
        var world = new World().WithStore(LastReconciled);
        world.Leases.Held = true;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => world.Reconcile.RunAsync(world.Connection, Ct));

        Assert.Equal(ShopifyErrorCodes.StoreSyncInProgress, error.Code);
        Assert.Empty(world.Source.Calls);
        // El lease es de la otra corrida: soltarlo la dejaría solaparse con la siguiente.
        Assert.Equal(0, world.Leases.Released);
        Assert.Empty(world.Leases.Reconciled);
    }

    // La ventana avanza al instante en que empezó la corrida, no al que terminó: un pedido que cambió mientras corría (después de
    // traer su página) entra en la ventana de la siguiente.
    [Fact]
    public async Task ReconcileSetsLastReconciledAt()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1)], null));
        world.Source.OnFetch = () => world.Clock.UtcNow = Now.AddMinutes(20);

        await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal(1, world.Leases.Released);
        Assert.Equal([(TenantId, ConnectionId, ReconcileStore.LeaseDuration)], world.Leases.Acquired);
        // Suelta el lease que tomó, no el que haya en la fila (minor 5).
        Assert.Equal([FakeReconcileLeases.Token], world.Leases.ReleasedTokens);
    }

    // §5.3, recuperación: el pedido cambió durante una corrida larga, cuando su página ya se había traído, y su webhook se perdió
    // (abandonado a los 8 intentos o descartado con 200). La siguiente corrida lo trae igual, porque su ventana arranca 5 minutos
    // antes de que empezara la anterior.
    [Fact]
    public async Task ReconcileOrderUpdatedDuringALongRunIsFetchedByTheNext()
    {
        var world = new World().WithStore(LastReconciled);
        world.Source.FilterBySince = true;
        world.Source.OnFetch = () =>
        {
            if (world.Source.Calls.Count == 1)
            {
                world.Source.Catalog.Add(Order(77, updatedAt: Now.AddMinutes(1)));
                world.Clock.UtcNow = Now.AddMinutes(30);
            }
        };

        await world.Reconcile.RunAsync(world.Connection, Ct);
        Assert.Empty(world.Database.Orders);
        await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal(Now.AddMinutes(-5), world.Source.Calls[^1].Since);
        Assert.Contains((ConnectionId, 77L), world.Database.Orders.Keys);
    }

    // Un pedido que falla (la reacción de §6.3 cae) no frena a los demás, descarta lo que dejó pendiente para que el guardado del
    // siguiente no lo confirme, y la ventana no avanza: la próxima corrida lo vuelve a traer.
    [Fact]
    public async Task ReconcileFailedOrderContinuesDiscardsAndKeepsTheWindow()
    {
        var world = new World().WithStore(LastReconciled).WithImported(1);
        world.Reaction.FailuresLeft = int.MaxValue;
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1, financialStatus: "refunded"), Order(2)], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Contains((ConnectionId, 2L), world.Database.Orders.Keys);
        Assert.True(world.UnitOfWork.Discards >= 1);
        Assert.Empty(world.Leases.Reconciled);
        Assert.Equal(1, world.Leases.Released);
        Assert.Equal((2, 1), (result.Fetched, result.Failed));
    }

    // La tienda de una conexión que se borró y se volvió a enlazar nace sin configurar, y el pedido viejo ya está en QEP. Si llega
    // cancelado sobre una fila en espera, la reacción no puede anularlo sin el asesor: la fila espera con store_not_configured y
    // la ventana avanza. Antes la reacción lanzaba, la corrida fallaba y la ventana quedaba congelada mientras la tienda siguiera
    // sin configurar.
    [Fact]
    public async Task ReconcileCancelledWaitingOrderWithAnUnconfiguredStoreMovesTheWindow()
    {
        var world = new World(realReaction: true).WithStore(LastReconciled).WithWaiting(1, Now.AddDays(-1));
        world.Writer.ByExternalKey["954889:1"] =
            ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0042", 100_000m, alreadyExisted: true, Guid.CreateVersion7());
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1) with { CancelledAt = Now.AddMinutes(-3) }], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal([Now], world.Leases.Reconciled);
        Assert.Equal((0, 1), (result.Failed, result.Waiting));
        var row = world.Database.Orders[(ConnectionId, 1L)];
        Assert.Equal((ShopifyImportState.Waiting, ShopifyWaitReasons.StoreNotConfigured), (row.ImportState, row.WaitReason));
        Assert.Empty(world.Writer.Cancels);
    }

    [Fact]
    public async Task ReconcileCountsTheFinalStateOfEveryOrderItTouched()
    {
        var world = new World().WithStore(LastReconciled).WithWaiting(4, Now.AddDays(-1));
        world.Importer.Outcome = row => row.ShopifyOrderId switch
        {
            1 => ShopifyImportState.Imported,
            3 => ShopifyImportState.Ignored,
            _ => ShopifyImportState.Waiting,
        };
        world.Source.Pages.Enqueue(new ShopifyOrdersPage([Order(1), Order(2), Order(3)], null));

        var result = await world.Reconcile.RunAsync(world.Connection, Ct);

        Assert.Equal((3, 1, 2, 0, 1), (result.Fetched, result.Imported, result.Waiting, result.Cancelled, result.Ignored));
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task ReconcileNotOperationalConnectionThrowsWithoutCallingShopify()
    {
        var world = new World().WithStore(LastReconciled);
        var paused = world.Connection with { IsOperational = false, AccessToken = null };

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Reconcile.RunAsync(paused, Ct));

        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, error.Code);
        Assert.Empty(world.Source.Calls);
        Assert.Empty(world.Leases.Acquired);
    }

    private static ShopifyOrderSnapshot Order(
        long id, DateTimeOffset? updatedAt = null, string financialStatus = "pending", string currency = "COP") =>
        ImporterWorld.Order(orderId: id, financialStatus: financialStatus, currency: currency) with { UpdatedAt = updatedAt ?? Now.AddMinutes(-2) };

    private sealed class World
    {
        /// <param name="realReaction">Con <c>true</c>, la ingesta usa la reacción de §6.3 de verdad, sobre <see cref="Writer"/>,
        /// en vez del doble que sólo anota.</param>
        public World(bool realReaction = false)
        {
            UnitOfWork = new FakeUnitOfWork(Database);
            Reaction = new FakeOrderChangeReaction(UnitOfWork);
            Writer = new FakeOrderWriter(UnitOfWork);
            var stores = new FakeStoreRepository(Database);
            var mirrors = new FakeOrderMirrorRepository(Database);
            IShopifyOrderChangeReaction reaction = realReaction ? new ShopifyOrderChanges(Writer, new FakeCustomerDirectory(), Clock) : Reaction;
            Ingestion = new ShopifyOrderIngestion(stores, mirrors, Importer, reaction, UnitOfWork, Clock);
            Leases = new FakeReconcileLeases(Database);
            Reconcile = new ReconcileStore(Source, Ingestion, stores, mirrors, Leases, Health, UnitOfWork, Clock, Time, Logs);
        }

        public FakeShopifyDatabase Database { get; } = new();

        public FakeClock Clock { get; } = new(Now);

        public FakeTimeProvider Time { get; } = new();

        public RecordingLogger<ReconcileStore> Logs { get; } = new();

        public FakeOrdersSource Source { get; } = new();

        public FakeReconcileLeases Leases { get; }

        public FakeConnectionHealth Health { get; } = new();

        public StatefulOrderImporter Importer { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; }

        public FakeOrderChangeReaction Reaction { get; }

        public FakeOrderWriter Writer { get; }

        public ReconcileStore Reconcile { get; }

        public ShopifyOrderIngestion Ingestion { get; }

        public ShopifyStore? Store { get; private set; }

        public ShopifyConnectionRef Connection { get; } =
            new(TenantId, ConnectionId, "954889", "mi-tienda.myshopify.com", true, "shpat_x");

        public World WithStore(DateTimeOffset lastReconciledAt)
        {
            Store = ShopifyStore.Create(ConnectionId, TenantId, lastReconciledAt);
            Database.Stores[ConnectionId] = Store;
            return this;
        }

        public World WithWaiting(long orderId, DateTimeOffset createdAt, string? payload = null)
        {
            var snapshot = Order(orderId) with { CreatedAt = createdAt, UpdatedAt = createdAt };
            var row = ShopifyOrderMirror.Create(
                TenantId, ConnectionId, orderId, payload ?? ShopifyOrderIngestion.WritePayload(snapshot), snapshot.UpdatedAt, snapshot.ToHeader());
            Database.Orders[(ConnectionId, orderId)] = row;
            return this;
        }

        public World WithImported(long orderId)
        {
            WithWaiting(orderId, Now.AddDays(-1));
            Database.Orders[(ConnectionId, orderId)].MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now.AddDays(-1));
            return this;
        }
    }
}

/// <summary>Shopify en memoria: devuelve las páginas encoladas (o, con <see cref="FilterBySince"/>, lo del catálogo desde
/// <c>since</c>) y lanza las fallas encoladas antes que cualquier página.</summary>
internal sealed class FakeOrdersSource : IShopifyOrdersSource
{
    public Queue<ShopifyOrdersPage> Pages { get; } = new();

    public Queue<ShopifyOrdersSourceException> Failures { get; } = new();

    public List<ShopifyOrderSnapshot> Catalog { get; } = [];

    public bool FilterBySince { get; set; }

    public Action? OnFetch { get; set; }

    public List<(ShopifyConnectionRef Connection, DateTimeOffset Since, string? Cursor)> Calls { get; } = [];

    public Task<ShopifyOrdersPage> FetchUpdatedSinceAsync(
        ShopifyConnectionRef connection, DateTimeOffset since, string? cursor, CancellationToken cancellationToken)
    {
        Calls.Add((connection, since, cursor));
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        var page = FilterBySince
            ? new ShopifyOrdersPage([.. Catalog.Where(order => order.UpdatedAt >= since)], null)
            : Pages.TryDequeue(out var queued) ? queued : new ShopifyOrdersPage([], null);
        OnFetch?.Invoke();
        return Task.FromResult(page);
    }
}

/// <summary>Los leases y la ventana en la «base»: como el <c>UPDATE</c> real, <see cref="RecordReconciledAsync"/> mueve
/// <c>last_reconciled_at</c> de la tienda guardada (acá, reemplazándola por una con esa ventana).</summary>
internal sealed class FakeReconcileLeases(FakeShopifyDatabase database) : IShopifyReconcileLeases
{
    public bool Held { get; set; }

    public List<(Guid TenantId, Guid ConnectionId, TimeSpan Duration)> Acquired { get; } = [];

    /// <summary>Lo que devuelve <see cref="TryAcquireAsync"/>: el <c>reconcile_lease_until</c> que tomó.</summary>
    public static readonly DateTimeOffset Token = new(2026, 10, 18, 16, 30, 0, TimeSpan.Zero);

    public int Released { get; private set; }

    public List<DateTimeOffset> ReleasedTokens { get; } = [];

    public List<DateTimeOffset> Reconciled { get; } = [];

    public Task<DateTimeOffset?> TryAcquireAsync(Guid tenantId, Guid connectionId, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (Held)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }

        Acquired.Add((tenantId, connectionId, duration));
        return Task.FromResult<DateTimeOffset?>(Token);
    }

    public Task ReleaseAsync(Guid tenantId, Guid connectionId, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        Released++;
        ReleasedTokens.Add(leaseUntil);
        return Task.CompletedTask;
    }

    public Task RecordReconciledAsync(Guid tenantId, Guid connectionId, DateTimeOffset reconciledAt, CancellationToken cancellationToken)
    {
        Reconciled.Add(reconciledAt);
        if (database.Stores.TryGetValue(connectionId, out var store) && store.TenantId == tenantId)
        {
            var moved = ShopifyStore.Create(connectionId, tenantId, reconciledAt);
            if (store.ShopDomain is { } domain)
            {
                moved.RecordShopDomain(domain);
            }

            database.Stores[connectionId] = moved;
        }

        return Task.CompletedTask;
    }
}

/// <summary>El importador visto desde la conciliación: anota cada intento y deja la fila en el estado que diga
/// <see cref="Outcome"/> (por defecto, en espera).</summary>
internal sealed class StatefulOrderImporter : IShopifyOrderImporter
{
    public List<(ShopifyConnectionRef Connection, ShopifyOrderMirror Row)> Calls { get; } = [];

    public Func<ShopifyOrderMirror, ShopifyImportState> Outcome { get; set; } = _ => ShopifyImportState.Waiting;

    public Task<ShopifyImportOutcome> ImportAsync(
        ShopifyConnectionRef connection, ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        Calls.Add((connection, row));
        switch (Outcome(row))
        {
            case ShopifyImportState.Imported:
                row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, DateTimeOffset.UtcNow);
                break;
            case ShopifyImportState.Ignored:
                row.MarkIgnored();
                break;
            default:
                row.MarkWaiting(ShopifyWaitReasons.StoreNotConfigured, null, DateTimeOffset.UtcNow);
                break;
        }

        return Task.FromResult(new ShopifyImportOutcome(row.ImportState));
    }
}

/// <summary>Un <see cref="TimeProvider"/> que no duerme: cada temporizador anota su espera y dispara en el acto. El reloj de
/// <see cref="GetTimestamp"/> avanza con las esperas.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long ticks;

    public List<TimeSpan> Delays { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            Interlocked.Add(ref ticks, dueTime.Ticks);
            _ = Task.Run(() => callback(state));
        }

        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Guarda en memoria lo que se loguea, para afirmar sobre los avisos (y que no lleven datos del comprador).</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Warnings =>
        Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
