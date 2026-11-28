using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Messaging;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>Spec inventario básico, de punta a punta: cada módulo publica al outbox como lo haría y el procesador real aplica contra
/// las reservas de demanda y el ledger. Los eventos se escriben con el contrato de §5.1.</summary>
public sealed class BasicInventoryEndToEndTests
{
    private static readonly Guid Tenant = Guid.Parse(WmsApiHarness.TenantId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record OrderLine(Guid LineId, Guid? VariantId, decimal Quantity, string Kind = "Physical");

    private sealed class World(PostgreSqlContainer database, QepApiFactory factory)
    {
        private int _tick;

        public PostgreSqlContainer Database { get; } = database;

        public QepApiFactory Factory { get; } = factory;

        public WmsInventoryEventsProcessor Processor { get; } = factory.Services.GetRequiredService<WmsInventoryEventsProcessor>();

        public DateTimeOffset NextAt() => DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(++_tick);
    }

    private static async Task<World> OpenWorldAsync(IEnumerable<TenantModuleKey>? modules = null)
    {
        var (database, factory) = await OpenAsync(modules);
        await SeedCallerMembershipAsync(factory);
        return new World(database, factory);
    }

    private static async Task DisposeAsync(World world)
    {
        world.Factory.Dispose();
        await world.Database.DisposeAsync();
    }

    private static async Task<Guid> VariantAsync(World world, string code) =>
        await CreateVariantAsync(world.Factory, code, null, [12], name: "Producto " + code);

    private static OrderLine Line(Guid variant, decimal quantity) => new(Guid.CreateVersion7(), variant, quantity);

    private static object[] Lines(IEnumerable<OrderLine> lines) =>
        [.. lines.Select(line => new { lineId = line.LineId, variantId = line.VariantId, quantity = line.Quantity, productKind = line.Kind })];

    private static async Task PublishAsync(World world, string eventName, Guid documentId, string idName, long version, Dictionary<string, object?> extra)
    {
        var at = world.NextAt();
        var payload = new Dictionary<string, object?>
        {
            ["tenantId"] = Tenant,
            [idName] = documentId,
            ["version"] = version,
            ["occurredAt"] = at,
        };
        foreach (var (key, value) in extra)
        {
            payload[key] = value;
        }

        await ExecuteAsync(
            world.Database,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @event, @payload::jsonb, @correlation, @occurredAt, 0)
            """,
            ("id", Guid.CreateVersion7()),
            ("event", eventName),
            ("payload", JsonSerializer.Serialize(payload)),
            ("correlation", documentId.ToString("D", CultureInfo.InvariantCulture)),
            ("occurredAt", at));
    }

    private static Task OrderEventAsync(
        World world,
        string suffix,
        Guid orderId,
        long version,
        IEnumerable<OrderLine>? lines = null,
        bool? isPrepared = null,
        bool? fromPlatform = null,
        string? origin = null,
        bool awaiting = false)
    {
        var extra = new Dictionary<string, object?>();
        if (lines is not null)
        {
            extra["lines"] = Lines(lines);
        }

        if (isPrepared is not null)
        {
            extra["isPrepared"] = isPrepared;
        }

        if (fromPlatform is not null)
        {
            extra["fromPlatform"] = fromPlatform;
        }

        if (origin is not null)
        {
            extra["origin"] = origin;
        }

        if (suffix == "created")
        {
            extra["awaitingWholesaleApproval"] = awaiting;
        }

        return PublishAsync(world, $"quotations.order.{suffix}.v1", orderId, "orderId", version, extra);
    }

    private static Task PurchaseReceivedAsync(World world, Guid orderId, IEnumerable<OrderLine> lines, string origin = "Manual") =>
        PublishAsync(
            world,
            "purchases.order.received.v1",
            orderId,
            "purchaseOrderId",
            2,
            new Dictionary<string, object?> { ["origin"] = origin, ["lines"] = Lines(lines) });

    private static Task PosCreatedAsync(World world, Guid saleId, IEnumerable<OrderLine> lines) =>
        PublishAsync(world, "pos.sale.created.v1", saleId, "saleId", 1, new Dictionary<string, object?> { ["lines"] = Lines(lines) });

    private static Task PosVoidedAsync(World world, Guid saleId) =>
        PublishAsync(world, "pos.sale.voided.v1", saleId, "saleId", 2, []);

    private static async Task DrainAsync(World world)
    {
        for (var pass = 0; pass < 20 && await world.Processor.ProcessBatchAsync(Ct); pass++)
        {
        }
    }

    private static Task<string> StateAsync(World world, string type, Guid id) =>
        ScalarAsync<string>(
            world.Database,
            "SELECT status FROM wms.document_stock_states WHERE tenant_id = @t AND document_type = @type AND document_id = @id",
            ("t", Tenant),
            ("type", type),
            ("id", id));

    private static Task<decimal> OnHandAsync(World world, Guid variant) =>
        ScalarAsync<decimal>(
            world.Database,
            """
            SELECT coalesce(sum(b.on_hand), 0) FROM wms.stock_balances b
            JOIN wms.warehouses w ON w.general_location_id = b.location_id
            WHERE b.tenant_id = @t AND b.variant_id = @v AND b.status = 'Available'
            """,
            ("t", Tenant),
            ("v", variant));

    private static Task<string> ReservationsAsync(World world, Guid orderId) =>
        ScalarAsync<string>(
            world.Database,
            """
            SELECT coalesce(string_agg(status || ':' || trim_scale(quantity)::text, ',' ORDER BY created_at, id), '')
            FROM wms.reservations WHERE tenant_id = @t AND demand_id = @d
            """,
            ("t", Tenant),
            ("d", orderId));

    private static Task<long> MovementsAsync(World world, string reason, string? documentType = null) =>
        ScalarAsync<long>(
            world.Database,
            "SELECT count(*) FROM wms.stock_movements WHERE tenant_id = @t AND reason = @r AND (@type::text IS NULL OR document_type = @type)",
            ("t", Tenant),
            ("r", reason),
            ("type", documentType));

    private static Task<long> PrincipalCountAsync(World world) =>
        ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.warehouses WHERE tenant_id = @t AND is_principal", ("t", Tenant));

    private static async Task<Guid> GeneralLocationAsync(World world) =>
        await ScalarAsync<Guid>(
            world.Database, "SELECT general_location_id FROM wms.warehouses WHERE tenant_id = @t AND is_principal", ("t", Tenant));

    [Fact]
    public async Task AReceivedPurchaseAddsToGeneral()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "REC1");
            var order = Guid.CreateVersion7();
            await PurchaseReceivedAsync(world, order, [Line(variant, 5)]);

            await DrainAsync(world);

            Assert.Equal(5m, await OnHandAsync(world, variant));
            Assert.Equal("Received", await StateAsync(world, "PurchaseOrder", order));
            Assert.Equal(1L, await PrincipalCountAsync(world));
            Assert.Equal(1L, await MovementsAsync(world, "Receipt", "PurchaseOrder"));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task AManualOriginReceiptIsSkippedWhenTheOrderHasAWmsReceipt()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "REC2");
            var order = Guid.CreateVersion7();
            await ExecuteAsync(
                world.Database,
                """
                INSERT INTO wms.document_stock_states (tenant_id, document_type, document_id, last_version, status, skipped_line_ids, updated_at)
                VALUES (@t, 'PurchaseOrder', @d, 1, 'ReceivedByWms', '[]'::jsonb, now())
                """,
                ("t", Tenant),
                ("d", order));
            await PurchaseReceivedAsync(world, order, [Line(variant, 5)]);

            await DrainAsync(world);

            Assert.Equal(0m, await OnHandAsync(world, variant));
            Assert.Equal("ReceivedByWms", await StateAsync(world, "PurchaseOrder", order));
            Assert.Equal(0L, await MovementsAsync(world, "Receipt"));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ACreatedOrderReservesAndPreparedTakesOut()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "ORD1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var lines = new[] { Line(variant, 3) };
            await OrderEventAsync(world, "created", order, 1, lines);
            await DrainAsync(world);

            Assert.Equal("Reserved", await StateAsync(world, "Order", order));
            Assert.Equal("Open:3", await ReservationsAsync(world, order));
            Assert.Equal(10m, await OnHandAsync(world, variant));

            await OrderEventAsync(world, "prepared", order, 2, lines, origin: "Manual");
            await DrainAsync(world);

            Assert.Equal("TakenOutByPrepare", await StateAsync(world, "Order", order));
            Assert.Equal("Consumed:3", await ReservationsAsync(world, order));
            Assert.Equal(7m, await OnHandAsync(world, variant));
            Assert.Equal(1L, await MovementsAsync(world, "Ship", "Order"));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task EditingReplacesTheReservation()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "EDT1");
            var order = Guid.CreateVersion7();
            var line = Line(variant, 3);
            await OrderEventAsync(world, "created", order, 1, [line]);
            await OrderEventAsync(world, "edited", order, 2, [line with { Quantity = 5 }]);

            await DrainAsync(world);

            Assert.Equal("Released:3,Open:5", await ReservationsAsync(world, order));
            Assert.Equal("Reserved", await StateAsync(world, "Order", order));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task UnpreparingCompensatesExactlyThePostedBatchAndReservesAgain()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "UNP1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var lines = new[] { Line(variant, 3) };
            await OrderEventAsync(world, "created", order, 1, lines);
            await OrderEventAsync(world, "prepared", order, 2, lines, origin: "Manual");
            await OrderEventAsync(world, "unprepared", order, 3);

            await DrainAsync(world);

            Assert.Equal(10m, await OnHandAsync(world, variant));
            Assert.Equal("Reserved", await StateAsync(world, "Order", order));
            Assert.Equal("Consumed:3,Open:3", await ReservationsAsync(world, order));
            Assert.Equal(1L, await MovementsAsync(world, "Reversal", "Order"));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    world.Database,
                    "SELECT count(DISTINCT batch_id) FROM wms.stock_movements WHERE tenant_id = @t AND document_type = 'Order' AND document_id = @d",
                    ("t", Tenant),
                    ("d", order)));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ArchivingUnpreparedTakesOutAndUnarchivingReturns()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "ARC1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var lines = new[] { Line(variant, 4) };
            await OrderEventAsync(world, "created", order, 1, lines);
            await OrderEventAsync(world, "archived", order, 2, lines, isPrepared: false);
            await DrainAsync(world);

            Assert.Equal("TakenOutByArchive", await StateAsync(world, "Order", order));
            Assert.Equal(6m, await OnHandAsync(world, variant));

            await OrderEventAsync(world, "unarchived", order, 3, isPrepared: false);
            await DrainAsync(world);

            Assert.Equal("Reserved", await StateAsync(world, "Order", order));
            Assert.Equal(10m, await OnHandAsync(world, variant));
            Assert.Equal("Consumed:4,Open:4", await ReservationsAsync(world, order));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task APlatformCancelOfAPreparedOrderReturnsNothing()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "PLT1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var line = Line(variant, 3);
            await OrderEventAsync(world, "created", order, 1, [line]);
            await OrderEventAsync(world, "prepared", order, 2, [line], origin: "Manual");
            await OrderEventAsync(world, "edited", order, 3, [line with { Quantity = 6 }]);
            await OrderEventAsync(world, "cancelled", order, 4, fromPlatform: true);

            await DrainAsync(world);

            Assert.Equal("Released", await StateAsync(world, "Order", order));
            Assert.Equal(7m, await OnHandAsync(world, variant));
            Assert.Equal(0L, await MovementsAsync(world, "Reversal"));
            Assert.Equal("Consumed:3", await ReservationsAsync(world, order));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task AConsoleCancelReleases()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "CAN1");
            var order = Guid.CreateVersion7();
            await OrderEventAsync(world, "created", order, 1, [Line(variant, 3)]);
            await OrderEventAsync(world, "cancelled", order, 2, fromPlatform: false);

            await DrainAsync(world);

            Assert.Equal("Released", await StateAsync(world, "Order", order));
            Assert.Equal("Released:3", await ReservationsAsync(world, order));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task APosSaleTakesOutAndVoidReturns()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "POS1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var sale = Guid.CreateVersion7();
            await PosCreatedAsync(world, sale, [Line(variant, 4)]);
            await DrainAsync(world);

            Assert.Equal(6m, await OnHandAsync(world, variant));
            Assert.Equal("Shipped", await StateAsync(world, "PosSale", sale));

            await PosVoidedAsync(world, sale);
            await DrainAsync(world);

            Assert.Equal(10m, await OnHandAsync(world, variant));
            Assert.Equal("Voided", await StateAsync(world, "PosSale", sale));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ASaleOfANeverReceivedVariantGoesNegative()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "NEG1");
            await PosCreatedAsync(world, Guid.CreateVersion7(), [Line(variant, 3)]);

            await DrainAsync(world);

            Assert.Equal(-3m, await OnHandAsync(world, variant));

            // Un Move desde ese saldo negativo sigue frenado.
            var general = await GeneralLocationAsync(world);
            var receiving = await ScalarAsync<Guid>(
                world.Database,
                "SELECT l.id FROM wms.locations l JOIN wms.warehouses w ON w.id = l.warehouse_id WHERE w.is_principal AND l.code = 'REC-01' AND w.tenant_id = @t",
                ("t", Tenant));
            var owner = await ScalarAsync<Guid>(world.Database, "SELECT id FROM wms.owners WHERE tenant_id = @t AND kind = 'Self'", ("t", Tenant));
            var move = new LedgerEntry(
                new StockKey(owner, general, variant, null, StockStatus.Available),
                new StockKey(owner, receiving, variant, null, StockStatus.Available),
                1,
                null,
                MovementReason.InternalMove,
                null,
                DateTimeOffset.UtcNow);
            var batch = new LedgerBatch(
                Tenant, Guid.CreateVersion7(), Guid.NewGuid().ToString(), new LedgerActor(Guid.Parse(SubjectId), LedgerActorType.Human, null), [move]);

            var error = await Assert.ThrowsAsync<StockInsufficientException>(() => PostAsync(world.Factory, batch));

            Assert.Equal("wms.stock.insufficient", error.Code);
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task AServiceLineMovesNothing()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "SRV1");
            var order = Guid.CreateVersion7();
            var service = new OrderLine(Guid.CreateVersion7(), variant, 2, "Service");
            await OrderEventAsync(world, "created", order, 1, [service]);
            await OrderEventAsync(world, "prepared", order, 2, [service], origin: "Manual");

            await DrainAsync(world);

            Assert.Equal(string.Empty, await ReservationsAsync(world, order));
            Assert.Equal(0L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.stock_movements"));
            Assert.Equal("TakenOutByPrepare", await StateAsync(world, "Order", order));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ALotTrackedVariantIsSkippedAndTheRestPosts()
    {
        var world = await OpenWorldAsync();
        try
        {
            var tracked = await VariantAsync(world, "LOT1");
            var plain = await VariantAsync(world, "LOT2");
            await SetTrackingAsync(CreateManager(world.Factory), tracked, "Lot");
            var order = Guid.CreateVersion7();
            var trackedLine = Line(tracked, 2);
            await PurchaseReceivedAsync(world, order, [trackedLine, Line(plain, 4)]);

            await DrainAsync(world);

            Assert.Equal(4m, await OnHandAsync(world, plain));
            Assert.Equal(0m, await OnHandAsync(world, tracked));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    world.Database,
                    "SELECT count(*) FROM wms.document_stock_states WHERE document_id = @d AND skipped_line_ids @> @ids::jsonb",
                    ("d", order),
                    ("ids", JsonSerializer.Serialize(new[] { trackedLine.LineId }))));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task AnUnknownVariantIsSkippedNotRetried()
    {
        var world = await OpenWorldAsync();
        try
        {
            var order = Guid.CreateVersion7();
            await PurchaseReceivedAsync(world, order, [Line(Guid.CreateVersion7(), 2)]);

            await DrainAsync(world);

            Assert.Equal("Received", await StateAsync(world, "PurchaseOrder", order));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.inbox_messages WHERE processed_at IS NOT NULL"));
            Assert.Equal(0L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.stock_movements"));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ATenantWithoutTheModuleCreatesNothing()
    {
        var world = await OpenWorldAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Wms));
        try
        {
            var variant = await VariantAsync(world, "OFF1");
            var order = Guid.CreateVersion7();
            await OrderEventAsync(world, "created", order, 1, [Line(variant, 3)]);
            await PosCreatedAsync(world, Guid.CreateVersion7(), [Line(variant, 1)]);

            await DrainAsync(world);

            Assert.Equal(0L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.document_stock_states"));
            Assert.Equal(0L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.warehouses"));
            Assert.Equal(0L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.reservations"));
            Assert.Equal(
                2L,
                await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.inbox_messages WHERE processed_at IS NOT NULL"));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task EventsOfADocumentWithStateApplyWithTheModuleOff()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "OFF2");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var lines = new[] { Line(variant, 3) };
            await OrderEventAsync(world, "created", order, 1, lines);
            await DrainAsync(world);
            await ExecuteAsync(
                world.Database, "DELETE FROM tenancy.tenant_modules WHERE tenant_id = @t AND module_key = 'wms'", ("t", Tenant));

            await OrderEventAsync(world, "prepared", order, 2, lines, origin: "Manual");
            await DrainAsync(world);

            Assert.Equal("TakenOutByPrepare", await StateAsync(world, "Order", order));
            Assert.Equal(7m, await OnHandAsync(world, variant));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task TwoConcurrentFirstEventsProvisionOnePrincipal()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "CON1");
            var first = Guid.CreateVersion7();
            var second = Guid.CreateVersion7();
            var messages = new[]
            {
                Message(world, "pos.sale.created.v1", "saleId", first, variant),
                Message(world, "pos.sale.created.v1", "saleId", second, variant),
            };

            await Task.WhenAll(messages.Select(message => ApplyDirectlyAsync(world, message)));

            Assert.Equal(1L, await PrincipalCountAsync(world));
            Assert.Equal(1L, await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.warehouses WHERE tenant_id = @t", ("t", Tenant)));
            Assert.Equal("Shipped", await StateAsync(world, "PosSale", first));
            Assert.Equal("Shipped", await StateAsync(world, "PosSale", second));
            Assert.Equal(-2m, await OnHandAsync(world, variant));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task TheOldestActiveWarehouseBecomesPrincipal()
    {
        var world = await OpenWorldAsync();
        try
        {
            var manager = CreateManager(world.Factory);
            var older = await CreateWarehouseAsync(manager, "AAA");
            await Task.Delay(50, Ct);
            var newer = await CreateWarehouseAsync(manager, "BBB");
            var variant = await VariantAsync(world, "OLD1");

            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 2)]);
            await DrainAsync(world);

            Assert.Equal(
                older.Id,
                await ScalarAsync<Guid>(world.Database, "SELECT id FROM wms.warehouses WHERE tenant_id = @t AND is_principal", ("t", Tenant)));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.warehouses WHERE id = @id AND is_principal", ("id", newer.Id)));
            Assert.Equal(2m, await OnHandAsync(world, variant));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task ACountOnGeneralParksTheSaleUntilItCloses()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "CNT1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            await DrainAsync(world);
            var general = await GeneralLocationAsync(world);
            await ExecuteAsync(world.Database, "UPDATE wms.locations SET status = 'Counting' WHERE id = @id", ("id", general));
            var sale = Guid.CreateVersion7();
            await PosCreatedAsync(world, sale, [Line(variant, 3)]);

            await DrainAsync(world);

            Assert.Equal(10m, await OnHandAsync(world, variant));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.inbox_messages WHERE processed_at IS NULL") - 1L);

            await ExecuteAsync(world.Database, "UPDATE wms.locations SET status = 'Active' WHERE id = @id", ("id", general));
            await ExecuteAsync(world.Database, "UPDATE wms.inbox_messages SET claimed_until = now() - interval '1 second' WHERE processed_at IS NULL");
            await DrainAsync(world);

            Assert.Equal(7m, await OnHandAsync(world, variant));
            Assert.Equal("Shipped", await StateAsync(world, "PosSale", sale));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    [Fact]
    public async Task AReversalAfterAChangeOfPrincipalGoesToTheOriginalWarehouse()
    {
        var world = await OpenWorldAsync();
        try
        {
            var variant = await VariantAsync(world, "CHG1");
            await PurchaseReceivedAsync(world, Guid.CreateVersion7(), [Line(variant, 10)]);
            var order = Guid.CreateVersion7();
            var lines = new[] { Line(variant, 3) };
            await OrderEventAsync(world, "created", order, 1, lines);
            await OrderEventAsync(world, "prepared", order, 2, lines, origin: "Manual");
            await DrainAsync(world);
            var original = await ScalarAsync<Guid>(
                world.Database, "SELECT id FROM wms.warehouses WHERE tenant_id = @t AND is_principal", ("t", Tenant));

            var manager = CreateManager(world.Factory);
            var other = await CreateWarehouseAsync(manager, "OTR");
            var version = await ScalarAsync<long>(world.Database, "SELECT version FROM wms.warehouses WHERE id = @id", ("id", other.Id));
            using var put = new HttpRequestMessage(HttpMethod.Put, $"{WmsUrl()}/warehouses/{other.Id}/principal")
            {
                Content = JsonContent.Create(new { version }),
            };
            put.Headers.Add("X-Qep-Client", "web");
            (await manager.SendAsync(put, Ct)).EnsureSuccessStatusCode();

            await OrderEventAsync(world, "unprepared", order, 3);
            await DrainAsync(world);

            Assert.Equal(
                10m,
                await ScalarAsync<decimal>(
                    world.Database,
                    "SELECT coalesce(sum(b.on_hand), 0) FROM wms.stock_balances b WHERE b.warehouse_id = @w AND b.variant_id = @v AND b.status = 'Available' AND b.location_id = (SELECT general_location_id FROM wms.warehouses WHERE id = @w)",
                    ("w", original),
                    ("v", variant)));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(world.Database, "SELECT count(*) FROM wms.stock_balances WHERE warehouse_id = @w AND variant_id = @v", ("w", other.Id), ("v", variant)));
            Assert.Equal(
                original,
                await ScalarAsync<Guid>(
                    world.Database, "SELECT warehouse_id FROM wms.reservations WHERE demand_id = @d AND status = 'Open'", ("d", order)));
        }
        finally
        {
            await DisposeAsync(world);
        }
    }

    private static InventoryMessage Message(World world, string eventName, string idName, Guid documentId, Guid variant)
    {
        var at = world.NextAt();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tenantId"] = Tenant,
            [idName] = documentId,
            ["version"] = 1,
            ["occurredAt"] = at,
            ["lines"] = Lines([Line(variant, 1)]),
        });
        return new InventoryMessage(Guid.CreateVersion7(), eventName, payload, at, 1);
    }

    // Lo que hace el procesador por mensaje, sin el reclamo: dos scopes, cada uno con su transacción, para forzar la carrera.
    private static async Task ApplyDirectlyAsync(World world, InventoryMessage message)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IInventoryEventApplier>().ApplyAsync(message, Ct);
        await unitOfWork.SaveChangesAsync(Ct);
    }
}
