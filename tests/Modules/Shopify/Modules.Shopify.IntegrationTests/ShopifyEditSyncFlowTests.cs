using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Infrastructure.Workers;
using Modules.Storage.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-14 §3 y §4 de punta a punta: un <c>orders/updated</c> sobre un pedido ya importado llega como cambio de Shopify
/// (pendiente, bloqueado o aplicado según la tienda), por el mismo webhook y el mismo worker que la importación.
/// </summary>
public sealed class ShopifyEditSyncFlowTests
{
    private const long OrderId = 5550000000001;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Fixture(Action<JsonObject>? edit = null)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        edit?.Invoke(order);
        return order;
    }

    /// <summary>La misma orden, editada en Shopify a una cantidad vigente de 1 (V-11: <c>current_quantity</c>).</summary>
    private static JsonObject QuantityEdited(string updatedAt = "2026-10-01T10:30:00-05:00") =>
        Fixture(order =>
        {
            order["updated_at"] = updatedAt;
            order["line_items"]![0]!["current_quantity"] = 1;
        });

    [Fact]
    public async Task AQuantityEditInShopifyCreatesAPendingChange()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());

        Assert.Equal("1|Pending", await flow.ScalarAsync("SELECT count(*) || '|' || max(status) FROM shopify.order_edits"));
        Assert.Equal("2", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
    }

    [Fact]
    public async Task AnAutomaticStoreAppliesTheEdit()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.SetEditSyncAsync("Automatic");

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());

        Assert.Equal("1", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
        Assert.Equal(
            "1",
            await flow.ScalarAsync("SELECT count(*)::text FROM quotations.order_history WHERE event_type = 'ItemsChanged' AND member_id IS NULL"));
        Assert.Equal("Applied", await flow.ScalarAsync("SELECT status FROM shopify.order_edits"));
        Assert.Equal("1", await flow.ScalarAsync(
            "SELECT (applied_projection -> 'lines' -> 0 ->> 'quantity') FROM shopify.orders"));
    }

    [Fact]
    public async Task DeliveringTheSameUpdateTwiceCreatesOneChange()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        await flow.DeliverAsync("orders/updated", "wh-3", QuantityEdited());

        Assert.Equal("1", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_edits"));
    }

    // Plan D6: un pedido importado antes de la sincronización no tiene versión aplicada ni proyección, y su nota no está en
    // buyer_notes. Una nota nueva de Shopify se aplica sin conflicto.
    [Fact]
    public async Task AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture(order => order["note"] = "Dejar en portería"));
        await flow.SetEditSyncAsync("Automatic");
        await ExecuteAsync(flow.ConnectionString, "UPDATE shopify.orders SET applied_payload = NULL, applied_projection = NULL");
        await ExecuteAsync(flow.ConnectionString, "UPDATE quotations.orders SET buyer_notes = NULL");

        await flow.DeliverAsync("orders/updated", "wh-2", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["note"] = "Llamar antes";
        }));

        Assert.Equal("Applied", await flow.ScalarAsync("SELECT status FROM shopify.order_edits"));
        Assert.Equal("0", await flow.ScalarAsync(
            "SELECT count(*)::text FROM shopify.order_edits, jsonb_array_elements(groups) AS g WHERE g ->> 'outcome' = 'Conflict'"));
        Assert.Equal("Llamar antes", await flow.ScalarAsync("SELECT buyer_notes FROM quotations.orders"));
    }

    /// <summary>Un tenant con la tienda enlazada y configurada, como el de <c>ShopifyImportFlowTests</c>, con el webhook y el
    /// worker corridos a mano.</summary>
    internal sealed class Flow : IAsyncDisposable
    {
        private readonly TestDatabase database;

        private Flow(TestDatabase database, QepApiFactory factory, ShopifyWorld world)
        {
            this.database = database;
            Factory = factory;
            World = world;
            Webhooks = factory.CreateClient();
        }

        public QepApiFactory Factory { get; }

        public ShopifyWorld World { get; }

        public HttpClient Webhooks { get; }

        public string ConnectionString => database.GetConnectionString();

        public static async Task<Flow> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString())
            {
                ConfigureTestServices = services =>
                {
                    services.RemoveAll<IObjectStorage>();
                    services.AddSingleton<IObjectStorage>(new InMemoryObjectStorage());
                },
            };
            var world = await ShopifyWorld.ArrangeAsync(factory, database.GetConnectionString());
            await world.ConfigureStoreAsync(factory);
            return new Flow(database, factory, world);
        }

        public HttpClient Client(params string[] permissions) =>
            CreateClient(Factory, World.Tenant.OwnerUserId, World.TenantId, [.. SeedPermissions, .. permissions]);

        /// <summary>El webhook firmado y el worker corrido hasta vaciar la cola.</summary>
        public async Task DeliverAsync(string topic, string webhookId, JsonObject order)
        {
            var response = await PostWebhookAsync(Webhooks, topic, webhookId, Encoding.UTF8.GetBytes(order.ToJsonString()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var worker = ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(Factory.Services);
            await worker.DrainAsync(Ct);
        }

        // El PUT de la tienda ya lo prueba ShopifyStoresApiTests; acá sólo hace falta la tienda en ese modo.
        public Task SetEditSyncAsync(string editSync) =>
            ExecuteAsync(ConnectionString, "UPDATE shopify.stores SET edit_sync = @sync", ("sync", editSync));

        public Task<string> ScalarAsync(string sql) => ScalarAsync<string>(ConnectionString, sql);

        public async Task<string?> MirrorAsync(string expression)
        {
            await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand(
                $"SELECT ({expression})::text FROM shopify.orders WHERE shopify_order_id = @id", connection);
            command.Parameters.AddWithValue("id", OrderId);
            var value = await command.ExecuteScalarAsync(Ct);
            return value is null or DBNull ? null : (string)value;
        }

        public async ValueTask DisposeAsync()
        {
            Webhooks.Dispose();
            World.Tenant.Seeder.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
