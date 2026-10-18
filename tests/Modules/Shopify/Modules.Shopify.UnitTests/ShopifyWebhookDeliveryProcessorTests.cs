using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Audit.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Parsing;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.2, §5.4 y §5.5: qué hace el worker con cada entrega. Lo que no se puede reintentar con provecho (un
/// payload que el parser no lee o una cabecera que el dominio no guarda, Review Focus 2) termina procesado con <c>last_error</c>;
/// perder la carrera con la conciliación o el reintento manual relee la fila y vuelve a aplicar, sin marcar nada como fallido.</summary>
public sealed class ShopifyWebhookDeliveryProcessorTests
{
    private const string Shop = "mi-tienda.myshopify.com";
    private const long DeliveryId = 42L;

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnknownShopMarksIgnoredUnknownShop()
    {
        var world = new World();

        var result = await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);

        Assert.Equal(ShopifyIgnoredReasons.UnknownShop, result.IgnoredReason);
        Assert.Null(result.LastError);
        Assert.Empty(world.Database.Orders);
        Assert.Empty(world.Importer.Calls);
    }

    [Fact]
    public async Task AppUninstalledReportsCredentialsRejectedWithAppUninstalled()
    {
        var world = new World().WithConnection(operational: true);

        var result = await world.Processor.ProcessAsync(Delivery("app/uninstalled", """{"id":1,"domain":"mi-tienda.myshopify.com"}"""), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Equal([(TenantId, ConnectionId)], world.Health.Uninstalled);
        Assert.Empty(world.Health.Rejected);
        Assert.Empty(world.Database.Orders);
    }

    [Fact]
    public async Task OrdersTopicPausedConnectionUpsertsMirrorWithoutImport()
    {
        var world = new World().WithConnection(operational: false);

        var result = await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        var row = Assert.Single(world.Database.Orders.Values);
        Assert.Equal(5550000000001L, row.ShopifyOrderId);
        Assert.Equal("#1001", row.OrderName);
        Assert.Equal(ShopifyImportState.Waiting, row.ImportState);
        Assert.Empty(world.Importer.Calls);
        // §6.1: la tienda nace vacía en el primer contacto, aunque la conexión esté en pausa.
        Assert.True(world.Database.Stores.ContainsKey(ConnectionId));
    }

    [Fact]
    public async Task OrdersTopicOperationalUpsertsAndImports()
    {
        var world = new World().WithConnection(operational: true);

        var result = await world.Processor.ProcessAsync(Delivery("orders/paid", RestFixture("taxes-included")), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        var row = Assert.Single(world.Database.Orders.Values);
        var call = Assert.Single(world.Importer.Calls);
        Assert.Same(row, call.Row);
        Assert.Equal(ConnectionId, call.Store.ConnectionId);
        Assert.Equal(TenantId, call.Store.TenantId);
        Assert.Equal(5550000000001L, call.Snapshot.OrderId);
        // D-S23: la clave externa del pedido lleva el shopId de la conexión, que la tienda no guarda.
        var connection = Assert.Single(world.Importer.Connections);
        Assert.Equal((TenantId, ConnectionId, "954889"), (connection.TenantId, connection.ConnectionId, connection.ShopId));
        // §5.4: el payload del espejo es el snapshot normalizado, no el cuerpo crudo del webhook.
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("#1001", payload.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OrdersTopicImportedRowIsNotImportedAgain()
    {
        var world = new World().WithConnection(operational: true);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        var row = Assert.Single(world.Database.Orders.Values);
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);

        var result = await world.Processor.ProcessAsync(Delivery("orders/updated", RestFixture("taxes-included", updatedAt: "2026-10-01T11:00:00-05:00")), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Single(world.Importer.Calls);
        Assert.Equal(ShopifyImportState.Imported, row.ImportState);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero), row.ShopifyUpdatedAt);
    }

    [Fact]
    public async Task CustomersRedactAuditsOnly()
    {
        var world = new World().WithConnection(operational: true);
        var body = """
            {"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com",
             "customer":{"id":191167,"email":"john@example.com","phone":"555-625-1199"},
             "orders_to_redact":[299938,280263]}
            """;

        var result = await world.Processor.ProcessAsync(Delivery("customers/redact", body), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        var entry = Assert.Single(world.Database.Audit);
        Assert.Equal(ShopifyAuditActions.ComplianceCustomersRedact, entry.Action);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal(ConnectionId, entry.ActorId);
        Assert.Equal(AuditActorType.Integration, entry.ActorType);
        using var fields = JsonDocument.Parse(entry.Fields);
        Assert.Equal(Shop, fields.RootElement.GetProperty("shopDomain").GetString());
        Assert.Equal(191167L, fields.RootElement.GetProperty("shopifyCustomerId").GetInt64());
        Assert.Equal([299938L, 280263L], fields.RootElement.GetProperty("shopifyOrderIds").EnumerateArray().Select(id => id.GetInt64()));
        // QEP no guarda en la auditoría el dato personal que la solicitud pide borrar.
        Assert.DoesNotContain("john@example.com", entry.Fields, StringComparison.Ordinal);
        Assert.DoesNotContain("555-625-1199", entry.Fields, StringComparison.Ordinal);
        Assert.Empty(world.Database.DeletedOrdersOf);
        Assert.Empty(world.Database.DeletedDeliveriesOf);
    }

    [Fact]
    public async Task CustomersDataRequestAuditsWithTheRequestedOrders()
    {
        var world = new World().WithConnection(operational: true);
        var body = """
            {"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com","orders_requested":[299938],
             "customer":{"id":191167,"email":"john@example.com","phone":"555-625-1199"},"data_request":{"id":9999}}
            """;

        var result = await world.Processor.ProcessAsync(Delivery("customers/data_request", body), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        var entry = Assert.Single(world.Database.Audit);
        Assert.Equal("shopify.compliance.data_request", entry.Action);
        using var fields = JsonDocument.Parse(entry.Fields);
        Assert.Equal([299938L], fields.RootElement.GetProperty("shopifyOrderIds").EnumerateArray().Select(id => id.GetInt64()));
    }

    [Fact]
    public async Task ShopRedactDeletesMirrorAndDeliveriesOfThatShop()
    {
        var world = new World().WithConnection(operational: false);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        Assert.Single(world.Database.Orders);

        var result = await world.Processor.ProcessAsync(Delivery("shop/redact", """{"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com"}"""), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Equal([(TenantId, ConnectionId)], world.Database.DeletedOrdersOf);
        Assert.Empty(world.Database.Orders);
        // La entrega en curso no se borra: el worker la cierra como procesada.
        Assert.Equal([(Shop, DeliveryId)], world.Database.DeletedDeliveriesOf);
        // §5.5: la tienda y la conexión quedan como estén.
        Assert.True(world.Database.Stores.ContainsKey(ConnectionId));
        Assert.Empty(world.Health.Uninstalled);
    }

    [Fact]
    public async Task OrdersTopicMalformedPayloadFailsPermanently()
    {
        var world = new World().WithConnection(operational: true);

        var result = await world.Processor.ProcessAsync(Delivery("orders/create", """{"id":5550000000001,"name":"#1001"}"""), Ct);

        Assert.Null(result.IgnoredReason);
        Assert.NotNull(result.LastError);
        Assert.StartsWith(ShopifyDeliveryResult.InvalidPayloadPrefix, result.LastError, StringComparison.Ordinal);
        Assert.Contains("created_at", result.LastError, StringComparison.Ordinal);
        Assert.Empty(world.Database.Orders);
        Assert.Empty(world.Importer.Calls);
    }

    [Theory]
    [InlineData("name", "#12345678901234567890123456789012345678901234567890123456789012345")]
    [InlineData("currency", "PESOS")]
    public async Task OrdersTopicHeaderTheDomainRejectsFailsPermanently(string field, string value)
    {
        var world = new World().WithConnection(operational: true);
        var body = JsonNode.Parse(RestFixture("taxes-included"))!;
        body[field] = value;

        var result = await world.Processor.ProcessAsync(Delivery("orders/create", body.ToJsonString()), Ct);

        Assert.NotNull(result.LastError);
        Assert.StartsWith(ShopifyDeliveryResult.InvalidPayloadPrefix, result.LastError, StringComparison.Ordinal);
        Assert.Empty(world.Database.Orders);
        Assert.Empty(world.Database.PendingOrders);
        Assert.Empty(world.Importer.Calls);
    }

    [Fact]
    public async Task OrdersTopicHeaderTheDomainRejectsOnAnExistingRowFailsPermanentlyAndKeepsTheRow()
    {
        var world = new World().WithConnection(operational: false);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        var body = JsonNode.Parse(RestFixture("taxes-included", updatedAt: "2026-10-01T11:00:00-05:00"))!;
        body["currency"] = "PESOS";

        var result = await world.Processor.ProcessAsync(Delivery("orders/updated", body.ToJsonString()), Ct);

        Assert.StartsWith(ShopifyDeliveryResult.InvalidPayloadPrefix, result.LastError, StringComparison.Ordinal);
        var row = Assert.Single(world.Database.Orders.Values);
        Assert.Equal("COP", row.Currency);
    }

    // Dos guardados sobre la misma fila: mientras el worker aplicaba el webhook, la conciliación guardó la fila (sube su versión) y
    // el guardado del worker choca con concurrency.conflict. El worker relee la fila del ganador y vuelve a aplicar su snapshot.
    [Fact]
    public async Task OrdersTopicConcurrencyConflictReloadsTheRowAndReapplies()
    {
        var world = new World().WithConnection(operational: false);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        var original = Assert.Single(world.Database.Orders.Values);
        var savesBefore = world.UnitOfWork.Saves;
        world.UnitOfWork.BeforeSave = save =>
        {
            if (save == savesBefore + 1)
            {
                // La otra escritura: la misma fila, leída por la conciliación y guardada primero con un motivo de espera.
                var winner = ShopifyOrderMirror.Create(
                    TenantId, ConnectionId, original.ShopifyOrderId, original.Payload, original.ShopifyUpdatedAt,
                    new ShopifyOrderHeader(original.OrderName, original.ShopifyCustomerId, original.ShopifyCreatedAt, original.CancelledAt,
                        original.FinancialStatus, original.Currency, original.TotalPrice, original.TaxesIncluded));
                winner.MarkWaiting(ShopifyWaitReasons.StoreNotConfigured, null, Now);
                world.Database.Orders[(ConnectionId, original.ShopifyOrderId)] = winner;
                throw new RequestConcurrencyException("concurrency.conflict", "The Shopify order changed meanwhile.");
            }

            return Task.CompletedTask;
        };

        var result = await world.Processor.ProcessAsync(
            Delivery("orders/updated", RestFixture("taxes-included", updatedAt: "2026-10-01T11:00:00-05:00")), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Equal(1, world.UnitOfWork.Discards);
        var row = Assert.Single(world.Database.Orders.Values);
        Assert.NotSame(original, row);
        // El snapshot del webhook quedó aplicado sobre la fila del ganador, sin pisar lo que el ganador escribió.
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero), row.ShopifyUpdatedAt);
        Assert.Equal(ShopifyWaitReasons.StoreNotConfigured, row.WaitReason);
    }

    [Fact]
    public async Task OrdersTopicConflictThatNeverClearsPropagatesForTheLeaseRetry()
    {
        var world = new World().WithConnection(operational: false);
        world.UnitOfWork.BeforeSave = _ => throw new RequestConcurrencyException("concurrency.conflict", "Still racing.");

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct));

        Assert.Equal(ShopifyOrderIngestion.MaxConcurrencyAttempts, world.UnitOfWork.Saves);
    }

    // Ruling S14: la reacción de §6.3 ve el estado anterior (ApplySnapshot pisa FinancialStatus y CancelledAt) y corre antes del
    // guardado que persiste el snapshot: snapshot y reacción entran juntos o no entra ninguno.
    [Fact]
    public async Task OrdersTopicExistingRowReactsWithThePreviousStateBeforeSaving()
    {
        var world = new World().WithConnection(operational: true);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        var row = Assert.Single(world.Database.Orders.Values);
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);
        var savesBefore = world.UnitOfWork.Saves;
        Assert.Empty(world.Reaction.Calls);

        var result = await world.Processor.ProcessAsync(
            Delivery("orders/cancelled", RestFixture("taxes-included", updatedAt: "2026-10-01T11:00:00-05:00", financialStatus: "refunded",
                cancelledAt: "2026-10-01T10:59:00-05:00")), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        var (change, current, savesSoFar) = Assert.Single(world.Reaction.Calls);
        Assert.Same(row, change.Row);
        Assert.Equal(new ShopifyMirrorState(ShopifyImportState.Imported, "paid", null), change.Previous);
        Assert.Equal(
            new ShopifyMirrorState(ShopifyImportState.Imported, "refunded", new DateTimeOffset(2026, 10, 1, 15, 59, 0, TimeSpan.Zero)),
            current);
        Assert.Equal(savesBefore, savesSoFar);
        Assert.Equal(ConnectionId, change.Store.ConnectionId);
        Assert.Equal("refunded", change.Snapshot.FinancialStatus);
    }

    [Fact]
    public async Task OrdersTopicReactionFailureSavesNothingSoTheRetrySeesTheChangeAgain()
    {
        var world = new World().WithConnection(operational: true);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        Assert.Single(world.Database.Orders.Values).MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Now);
        var savesBefore = world.UnitOfWork.Saves;
        world.Reaction.FailuresLeft = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Processor.ProcessAsync(
            Delivery("orders/paid", RestFixture("taxes-included", updatedAt: "2026-10-01T11:00:00-05:00", financialStatus: "refunded")), Ct));

        Assert.Equal(savesBefore, world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task OrdersTopicNewRowDoesNotReact()
    {
        var world = new World().WithConnection(operational: true);

        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);

        Assert.Empty(world.Reaction.Calls);
    }

    [Fact]
    public async Task OrdersTopicNewStoreRecordsTheShopDomain()
    {
        var world = new World().WithConnection(operational: false);

        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);

        Assert.Equal(Shop, world.Database.Stores[ConnectionId].ShopDomain);
    }

    // Ruling S16 / §5.5: shop/redact llega 48 horas después de desinstalar. Si la conexión se borró, la ruta de Integrations cayó
    // con ella; el módulo encuentra la tienda por su dominio y borra igual.
    [Fact]
    public async Task ShopRedactWithTheConnectionDeletedStillDeletesByDomain()
    {
        var world = new World().WithConnection(operational: false);
        await world.Processor.ProcessAsync(Delivery("orders/create", RestFixture("taxes-included")), Ct);
        world.Directory.Connections.Clear();

        var result = await world.Processor.ProcessAsync(Delivery("shop/redact", """{"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com"}"""), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Equal([(TenantId, ConnectionId)], world.Database.DeletedOrdersOf);
        Assert.Empty(world.Database.Orders);
        Assert.Equal([(Shop, DeliveryId)], world.Database.DeletedDeliveriesOf);
    }

    [Fact]
    public async Task ShopRedactOfAShopQepNeverSawStillDeletesItsDeliveries()
    {
        var world = new World();

        var result = await world.Processor.ProcessAsync(Delivery("shop/redact", """{"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com"}"""), Ct);

        Assert.Equal(ShopifyDeliveryResult.Processed, result);
        Assert.Empty(world.Database.DeletedOrdersOf);
        Assert.Equal([(Shop, DeliveryId)], world.Database.DeletedDeliveriesOf);
    }

    [Fact]
    public async Task UnhandledTopicIsIgnoredWithItsReason()
    {
        var world = new World().WithConnection(operational: true);

        var result = await world.Processor.ProcessAsync(Delivery("products/update", "{}"), Ct);

        Assert.Equal(ShopifyIgnoredReasons.UnsupportedTopic, result.IgnoredReason);
    }

    // Review Focus 2: firma válida y JSON inesperado. El worker no la deja reintentando: la cierra en el primer intento con
    // last_error, y el resto de la cola sigue.
    [Fact]
    public async Task WorkerMalformedPayloadMarksProcessedWithError()
    {
        var world = new World().WithConnection(operational: true);
        var queue = new FakeDeliveryQueue();
        queue.Enqueue(new ShopifyWebhookDelivery(1L, "orders/create", Shop, """{"id":"not-a-number","name":"#1001"}"""));
        queue.Enqueue(new ShopifyWebhookDelivery(2L, "orders/create", Shop, RestFixture("taxes-included")));
        var worker = world.Worker(queue);

        await worker.DrainAsync(Ct);

        var malformed = queue.Processed[1L];
        Assert.Equal(1, malformed.Attempts);
        Assert.Null(malformed.IgnoredReason);
        Assert.StartsWith(ShopifyDeliveryResult.InvalidPayloadPrefix, malformed.LastError, StringComparison.Ordinal);
        Assert.Equal((null, null, 1), queue.Processed[2L]);
        Assert.Empty(queue.Pending);
        Assert.Single(world.Database.Orders);
    }

    [Fact]
    public async Task WorkerUnknownShopClosesTheDeliveryWithItsReason()
    {
        var world = new World();
        var queue = new FakeDeliveryQueue();
        queue.Enqueue(new ShopifyWebhookDelivery(1L, "orders/create", Shop, RestFixture("taxes-included")));

        await world.Worker(queue).DrainAsync(Ct);

        Assert.Equal((ShopifyIgnoredReasons.UnknownShop, null, 1), queue.Processed[1L]);
    }

    // Una falla que no es del payload (la base, la red del adaptador) vuelve con el lease, y sólo al octavo intento se cierra.
    [Fact]
    public async Task WorkerTransientFailureRetriesUntilTheEighthAttempt()
    {
        var world = new World().WithConnection(operational: false);
        world.UnitOfWork.BeforeSave = _ => throw new InvalidOperationException("database is down");
        var queue = new FakeDeliveryQueue();
        queue.Enqueue(new ShopifyWebhookDelivery(1L, "orders/create", Shop, RestFixture("taxes-included")));
        var worker = world.Worker(queue);

        for (var attempt = 1; attempt < ShopifyWebhookDeliveryWorker.MaxAttempts; attempt++)
        {
            await worker.DrainAsync(Ct);
            Assert.Empty(queue.Processed);
            world.Clock.UtcNow += ShopifyWebhookDeliveryWorker.LeaseFor(attempt) + TimeSpan.FromSeconds(1);
        }

        await worker.DrainAsync(Ct);

        var closed = queue.Processed[1L];
        Assert.Equal(ShopifyWebhookDeliveryWorker.MaxAttempts, closed.Attempts);
        Assert.Contains("database is down", closed.LastError, StringComparison.Ordinal);
    }

    private static ShopifyWebhookDelivery Delivery(string topic, string payload) => new(DeliveryId, topic, Shop, payload);

    private static string RestFixture(string name, string? updatedAt = null, string? financialStatus = null, string? cancelledAt = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rest", name + ".json"));
        if (updatedAt is null && financialStatus is null && cancelledAt is null)
        {
            return text;
        }

        var node = JsonNode.Parse(text)!;
        if (updatedAt is not null)
        {
            node["updated_at"] = updatedAt;
        }

        if (financialStatus is not null)
        {
            node["financial_status"] = financialStatus;
        }

        if (cancelledAt is not null)
        {
            node["cancelled_at"] = cancelledAt;
            node["cancel_reason"] = "customer";
        }

        return node.ToJsonString();
    }

    private sealed class World
    {
        public World()
        {
            UnitOfWork = new FakeUnitOfWork(Database);
            Reaction = new FakeOrderChangeReaction(UnitOfWork);
            Ingestion = new ShopifyOrderIngestion(
                new FakeStoreRepository(Database), new FakeOrderMirrorRepository(Database), Importer, Reaction, UnitOfWork, Clock);
        }

        public FakeShopifyDatabase Database { get; } = new();

        public FakeConnectionDirectory Directory { get; } = new();

        public FakeConnectionHealth Health { get; } = new();

        public FakeOrderImporter Importer { get; } = new();

        public FakeClock Clock { get; } = new(Now);

        public FakeUnitOfWork UnitOfWork { get; }

        public ShopifyOrderIngestion Ingestion { get; }

        public FakeOrderChangeReaction Reaction { get; }

        public ShopifyWebhookDeliveryProcessor Processor => new(
            Directory,
            Health,
            new ShopifyOrderPayloadParser(),
            Ingestion,
            new FakeStoreRepository(Database),
            new FakeOrderMirrorRepository(Database),
            new FakeWebhookDeliveries(Database),
            new FakeAuditRecorder(Database),
            UnitOfWork,
            Clock);

        public World WithConnection(bool operational)
        {
            Directory.Connections[Shop] = new ShopifyConnectionRef(
                TenantId, ConnectionId, "954889", Shop, operational, operational ? "shpat_sentinel" : null);
            return this;
        }

        /// <summary>El worker real sobre un contenedor con estos dobles: cada entrega en su scope, como en el host.</summary>
        public ShopifyWebhookDeliveryWorker Worker(FakeDeliveryQueue queue)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IShopifyWebhookDeliveryQueue>(queue);
            services.AddScoped(_ => Processor);
            var provider = services.BuildServiceProvider();
            return new ShopifyWebhookDeliveryWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ShopifyWebhookOptions()),
                NullLogger<ShopifyWebhookDeliveryWorker>.Instance);
        }
    }
}
