using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §5.2 contra Postgres: el reclamo, el cierre con <c>ignored_reason</c>/<c>last_error</c>, el borrado de
/// <c>shop/redact</c> y la carrera por la misma fila del espejo. Los puertos hacia Integrations y el importador todavía no tienen
/// adaptador (tareas 12 y 13): entran como dobles, y el worker corre a mano (<c>DrainAsync</c>) para que la prueba decida cuándo.
/// </summary>
public sealed class ShopifyWebhookWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkerSignedOrderUpsertsMirrorAndClosesDelivery()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var ports = new Ports();
        using var factory = ports.Factory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        ports.Directory.Connections[Shop] = new ShopifyConnectionRef(world.TenantId, world.ConnectionId, "954889", Shop, true, "shpat_sentinel_token");
        using var client = factory.CreateClient();

        var response = await PostWebhookAsync(client, "orders/create", "wh-1", OrderFixture("taxes-included"));
        await DrainAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1|none|none", await DeliveryAsync(connectionString, "wh-1"));
        Assert.Equal(
            $"{world.TenantId}|5550000000001|#1001|Waiting",
            await ScalarAsync<string>(
                connectionString,
                "SELECT tenant_id || '|' || shopify_order_id || '|' || order_name || '|' || import_state FROM shopify.orders WHERE connection_id = @c",
                ("c", world.ConnectionId)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c AND tenant_id = @t",
            ("c", world.ConnectionId), ("t", world.TenantId)));
        Assert.Single(ports.Importer.Calls);
    }

    [Fact]
    public async Task WorkerMalformedPayloadClosesWithLastErrorWithoutRetry()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var ports = new Ports().WithShop(Guid.CreateVersion7(), Guid.CreateVersion7());
        using var factory = ports.Factory(connectionString);
        using var client = factory.CreateClient();

        await PostWebhookAsync(client, "orders/create", "wh-bad", """{"id":5550000000001,"name":"#1001","updated_at":"ayer"}"""u8.ToArray());
        await DrainAsync(factory);

        var closed = await DeliveryAsync(connectionString, "wh-bad");
        Assert.StartsWith("1|none|invalid_payload", closed, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
    }

    // Dos guardados sobre la misma fila en Postgres: mientras el worker importa, otra escritura (la conciliación) sube la versión de
    // la fila. El guardado del worker choca con concurrency.conflict; el worker relee la fila y vuelve a aplicar, y la entrega
    // termina procesada sin last_error.
    [Fact]
    public async Task WorkerLostRaceOnTheSameRowReloadsAndReapplies()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var ports = new Ports().WithShop(tenantId, connectionId);
        ports.Importer.OnImport = async (row, call) =>
        {
            if (call == 1)
            {
                await ExecuteAsync(
                    connectionString,
                    "UPDATE shopify.orders SET version = version + 1, attempts = attempts + 1 WHERE id = @id",
                    ("id", row.Id));
            }

            row.MarkWaiting(ShopifyWaitReasons.StoreNotConfigured, null, DateTimeOffset.UtcNow);
        };
        using var factory = ports.Factory(connectionString);
        using var client = factory.CreateClient();

        await PostWebhookAsync(client, "orders/create", "wh-race", OrderFixture("taxes-included"));
        await DrainAsync(factory);

        Assert.Equal("1|none|none", await DeliveryAsync(connectionString, "wh-race"));
        Assert.Equal(2, ports.Importer.Calls.Count);
        // La fila del ganador (attempts = 1 por su escritura) más el intento que el worker aplicó al releer.
        Assert.Equal(
            "store_not_configured|2",
            await ScalarAsync<string>(connectionString, "SELECT wait_reason || '|' || attempts FROM shopify.orders"));
    }

    [Fact]
    public async Task WorkerShopRedactDeletesMirrorAndDeliveriesOfThatShopOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var ports = new Ports().WithShop(tenantId, connectionId, operational: false);
        using var factory = ports.Factory(connectionString);
        using var client = factory.CreateClient();
        await PostWebhookAsync(client, "orders/create", "wh-order", OrderFixture("taxes-included"));
        await PostWebhookAsync(client, "orders/create", "wh-other-shop", OrderFixture("taxes-included"), shop: "otra-tienda.myshopify.com");
        await DrainAsync(factory);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
        Assert.Equal("1|unknown_shop|none", await DeliveryAsync(connectionString, "wh-other-shop"));

        await PostWebhookAsync(client, "shop/redact", "wh-redact", """{"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com"}"""u8.ToArray());
        await DrainAsync(factory);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
        Assert.Equal(
            "wh-other-shop,wh-redact",
            await ScalarAsync<string>(connectionString, "SELECT string_agg(webhook_id, ',' ORDER BY webhook_id) FROM shopify.webhook_deliveries"));
        Assert.Equal("1|none|none", await DeliveryAsync(connectionString, "wh-redact"));
        // §5.5: la tienda queda como esté.
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    // Ruling S14 contra Postgres: la reacción de §6.3 falla una vez. El snapshot no se guardó sin ella, así que la entrega vuelve
    // con su lease, la fila sigue con el estado anterior y el reintento vuelve a ver el cambio.
    [Fact]
    public async Task WorkerFailedReactionIsSeenAgainOnRetry()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var ports = new Ports().WithShop(Guid.CreateVersion7(), Guid.CreateVersion7());
        ports.Importer.OnImport = (row, _) =>
        {
            row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        };
        ports.Reaction.FailuresLeft = 1;
        using var factory = ports.Factory(connectionString);
        using var client = factory.CreateClient();
        await PostWebhookAsync(client, "orders/create", "wh-create", OrderFixture("taxes-included"));
        await DrainAsync(factory);
        Assert.Equal("Imported|paid", await RowStateAsync(connectionString));

        await PostWebhookAsync(client, "orders/updated", "wh-refund", Refunded());
        await DrainAsync(factory);

        Assert.Equal("pending", await DeliveryAsync(connectionString, "wh-refund"));
        Assert.Equal("Imported|paid", await RowStateAsync(connectionString));

        await ExecuteAsync(connectionString, "UPDATE shopify.webhook_deliveries SET lease_until = now() - interval '1 second' WHERE webhook_id = 'wh-refund'");
        await DrainAsync(factory);

        Assert.Equal("2|none|none", await DeliveryAsync(connectionString, "wh-refund"));
        Assert.Equal("Imported|refunded", await RowStateAsync(connectionString));
        Assert.Equal(["paid", "paid"], ports.Reaction.Previous.Select(state => state.FinancialStatus));
    }

    // Ruling S16 contra Postgres: la conexión se borró y la ruta cayó en cascada; shop/redact encuentra la tienda por su dominio.
    [Fact]
    public async Task WorkerShopRedactWithTheConnectionDeletedDeletesByDomain()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var ports = new Ports().WithShop(Guid.CreateVersion7(), Guid.CreateVersion7(), operational: false);
        using var factory = ports.Factory(connectionString);
        using var client = factory.CreateClient();
        await PostWebhookAsync(client, "orders/create", "wh-order", OrderFixture("taxes-included"));
        await DrainAsync(factory);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
        ports.Directory.Connections.Clear();

        await PostWebhookAsync(client, "shop/redact", "wh-redact", """{"shop_id":954889,"shop_domain":"mi-tienda.myshopify.com"}"""u8.ToArray());
        await DrainAsync(factory);

        Assert.Equal("1|none|none", await DeliveryAsync(connectionString, "wh-redact"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
        Assert.Equal("wh-redact", await ScalarAsync<string>(connectionString, "SELECT string_agg(webhook_id, ',') FROM shopify.webhook_deliveries"));
        Assert.Equal(Shop, await ScalarAsync<string>(connectionString, "SELECT shop_domain FROM shopify.stores"));
    }

    private static byte[] Refunded()
    {
        var node = JsonNode.Parse(OrderFixture("taxes-included"))!;
        node["updated_at"] = "2026-10-01T11:00:00-05:00";
        node["financial_status"] = "refunded";
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static Task<string> RowStateAsync(string connectionString) =>
        ScalarAsync<string>(connectionString, "SELECT import_state || '|' || coalesce(financial_status, '') FROM shopify.orders");

    private static async Task DrainAsync(QepApiFactory factory)
    {
        var worker = ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(factory.Services);
        await worker.DrainAsync(Ct);
    }

    /// <summary><c>attempts|ignored_reason|last_error</c> de una entrega ya cerrada; <c>pending</c> si sigue abierta.</summary>
    private static Task<string> DeliveryAsync(string connectionString, string webhookId) =>
        ScalarAsync<string>(
            connectionString,
            """
            SELECT CASE WHEN processed_at IS NULL THEN 'pending'
                        ELSE attempts || '|' || coalesce(ignored_reason, 'none') || '|' || coalesce(last_error, 'none') END
            FROM shopify.webhook_deliveries WHERE webhook_id = @id
            """,
            ("id", webhookId));

    private sealed class Ports
    {
        public FakeDirectory Directory { get; } = new();

        public FakeHealth Health { get; } = new();

        public RecordingImporter Importer { get; } = new();

        public FailingOnceReaction Reaction { get; } = new();

        public Ports WithShop(Guid tenantId, Guid connectionId, bool operational = true)
        {
            Directory.Connections[Shop] = new ShopifyConnectionRef(tenantId, connectionId, "954889", Shop, operational, operational ? "shpat_x" : null);
            return this;
        }

        /// <summary>El host con el procesamiento del webhook y estos dobles para los puertos que todavía no tienen adaptador.
        /// Sin el worker en segundo plano: la prueba lo corre a mano.</summary>
        public QepApiFactory Factory(string connectionString) => new(connectionString)
        {
            ConfigureTestServices = services =>
            {
                services.AddShopifyWebhookProcessing();
                services.AddSingleton<IShopifyConnectionDirectory>(Directory);
                services.AddSingleton<IShopifyConnectionHealth>(Health);
                services.AddSingleton<IShopifyOrderImporter>(Importer);
                services.AddSingleton<IShopifyOrderChangeReaction>(Reaction);
            },
        };
    }

    private sealed class FakeDirectory : IShopifyConnectionDirectory
    {
        public Dictionary<string, ShopifyConnectionRef> Connections { get; } = new(StringComparer.Ordinal);

        public Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken) =>
            Task.FromResult(Connections.GetValueOrDefault(shopDomain));
    }

    private sealed class FakeHealth : IShopifyConnectionHealth
    {
        public Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingOnceReaction : IShopifyOrderChangeReaction
    {
        public List<ShopifyMirrorState> Previous { get; } = [];

        public int FailuresLeft { get; set; }

        public Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken)
        {
            Previous.Add(change.Previous);
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("Quotations is down.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImporter : IShopifyOrderImporter
    {
        public List<long> Calls { get; } = [];

        public Func<ShopifyOrderMirror, int, Task>? OnImport { get; set; }

        public async Task<ShopifyImportOutcome> ImportAsync(
            ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(row.ShopifyOrderId);
            }

            if (OnImport is { } hook)
            {
                await hook(row, Calls.Count);
            }

            return new ShopifyImportOutcome(row.ImportState);
        }
    }
}
