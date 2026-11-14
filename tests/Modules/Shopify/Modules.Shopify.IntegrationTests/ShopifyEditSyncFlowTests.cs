using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
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

    /// <summary>La misma orden con otra cantidad vigente, más tarde: la edición siguiente del mismo grupo.</summary>
    private static JsonObject QuantityEditedAgain() =>
        Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T11:00:00-05:00";
            order["line_items"]![0]!["current_quantity"] = 3;
        });

    // Review I-1: el pedido cambia en Quotations, que confirma por su cuenta. Si lo que viene después falla (aquí, la reacción de
    // §6.3), el reintento no puede dejar la proyección vieja: la edición siguiente de Shopify sería un conflicto falso.
    [Fact]
    public async Task AnAutomaticApplyWhoseProcessingFailsEndsAppliedOnRetry()
    {
        var reactionFailures = new ReactionFailures();
        await using var flow = await Flow.StartAsync(services =>
        {
            services.AddSingleton(reactionFailures);
            services.AddScoped<ShopifyOrderChanges>();
            services.RemoveAll<IShopifyOrderChangeReaction>();
            services.AddScoped<IShopifyOrderChangeReaction>(provider =>
                new FailingOrderChangeReaction(provider.GetRequiredService<ShopifyOrderChanges>(), reactionFailures));
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.SetEditSyncAsync("Automatic");
        reactionFailures.Remaining = 1;

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        await flow.DeliverAsync("orders/updated", "wh-3", QuantityEdited());

        Assert.Equal("Applied", await flow.ScalarAsync("SELECT coalesce(string_agg(status, ','), '(none)') FROM shopify.order_edits"));
        Assert.Equal("1", await flow.ScalarAsync(
            "SELECT trim_scale((applied_projection -> 'lines' -> 0 ->> 'quantity')::numeric)::text FROM shopify.orders"));
        await AssertTheNextQuantityEditAppliesAsync(flow);
    }

    // Review I-1: el guardado que marca el cambio aplicado falla después de que Quotations confirmó el pedido.
    [Fact]
    public async Task AnAutomaticApplyWhoseFinalSaveFailsEndsAppliedOnRetry()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.SetEditSyncAsync("Automatic");
        await flow.FailTheNextAppliedChangeAsync();

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        Assert.Equal("1", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
        await flow.DeliverAsync("orders/updated", "wh-3", QuantityEdited());

        Assert.Equal("Applied", await flow.ScalarAsync("SELECT coalesce(string_agg(status, ','), '(none)') FROM shopify.order_edits"));
        Assert.Equal("1", await flow.ScalarAsync(
            "SELECT trim_scale((applied_projection -> 'lines' -> 0 ->> 'quantity')::numeric)::text FROM shopify.orders"));
        await AssertTheNextQuantityEditAppliesAsync(flow);
    }

    // Review I-1, al aceptar: Quotations confirma, el guardado del cambio falla y la persona vuelve a aceptar.
    [Fact]
    public async Task AnAcceptWhoseFinalSaveFailsEndsAppliedWhenAcceptedAgain()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var accept = $"{ChangesRoute(flow.World.TenantId)}/{await flow.ScalarAsync("SELECT id::text FROM shopify.order_edits")}/accept";
        await flow.FailTheNextAppliedChangeAsync();

        var failed = await client.PostAsync(accept, content: null, Ct);
        Assert.False(failed.IsSuccessStatusCode);
        Assert.Equal("1", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
        var accepted = await client.PostAsync(accept, content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Applied", await flow.ScalarAsync("SELECT coalesce(string_agg(status, ','), '(none)') FROM shopify.order_edits"));
        Assert.Equal("1", await flow.ScalarAsync(
            "SELECT trim_scale((applied_projection -> 'lines' -> 0 ->> 'quantity')::numeric)::text FROM shopify.orders"));
        await flow.SetEditSyncAsync("Automatic");
        await AssertTheNextQuantityEditAppliesAsync(flow);
    }

    // La edición siguiente de Shopify sobre las líneas se aplica: el asesor no las tocó, así que no es conflicto qep_changed.
    private static async Task AssertTheNextQuantityEditAppliesAsync(Flow flow)
    {
        await flow.DeliverAsync("orders/updated", "wh-next", QuantityEditedAgain());

        Assert.Equal("0", await flow.ScalarAsync(
            "SELECT count(*)::text FROM shopify.order_edits, jsonb_array_elements(groups) AS g WHERE g ->> 'outcome' = 'Conflict'"));
        Assert.Equal("3", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
    }

    private static void Renumber(JsonObject order, long id, string name)
    {
        order["id"] = id;
        order["admin_graphql_api_id"] = $"gid://shopify/Order/{id}";
        order["name"] = name;
    }

    private static string ChangesRoute(Guid tenantId) => $"/api/v1/tenants/{tenantId}/shopify/changes";

    // Spec 2026-11-14 §4: el flujo del owner para un pedido preparado que no ha salido: deshacer la preparación y aceptar.
    [Fact]
    public async Task APreparedOrderBlocksAndAcceptAfterUnprepareApplies()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var orderId = await flow.ScalarAsync("SELECT id::text FROM quotations.orders");
        var orders = $"/api/v1/tenants/{flow.World.TenantId}/orders/{orderId}";
        Assert.True((await client.PostAsync($"{orders}/prepare", content: null, Ct)).IsSuccessStatusCode);

        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());

        Assert.Equal("Blocked|order_not_editable", await flow.ScalarAsync("SELECT status || '|' || status_reason FROM shopify.order_edits"));
        var changeId = await flow.ScalarAsync("SELECT id::text FROM shopify.order_edits");
        var accept = $"{ChangesRoute(flow.World.TenantId)}/{changeId}/accept";
        var refused = await client.PostAsync(accept, content: null, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("shopify.edit.order_not_editable", (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());

        Assert.True((await client.PostAsync($"{orders}/unprepare", content: null, Ct)).IsSuccessStatusCode);
        var accepted = await client.PostAsync(accept, content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Applied", (await accepted.Content.ReadFromJsonAsync<JsonObject>(Ct))!["status"]!.GetValue<string>());
        Assert.Equal("1", await flow.ScalarAsync("SELECT trim_scale(quantity)::text FROM quotations.order_items"));
        Assert.Equal("1", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_edits WHERE decided_by IS NOT NULL"));
    }

    [Fact]
    public async Task ListChangesAndSummaryCountPendingAndBlocked()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-a", Fixture(order => Renumber(order, 5550000000401, "#1401")));
        await flow.DeliverAsync("orders/create", "wh-b", Fixture(order => Renumber(order, 5550000000402, "#1402")));
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var prepared = await flow.ScalarAsync(
            "SELECT qep_order_id::text FROM shopify.orders WHERE shopify_order_id = 5550000000402");
        Assert.True((await client.PostAsync(
            $"/api/v1/tenants/{flow.World.TenantId}/orders/{prepared}/prepare", content: null, Ct)).IsSuccessStatusCode);

        await flow.DeliverAsync("orders/updated", "wh-c", Fixture(order =>
        {
            Renumber(order, 5550000000401, "#1401");
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["note"] = "Llamar antes";
        }));
        await flow.DeliverAsync("orders/updated", "wh-d", Fixture(order =>
        {
            Renumber(order, 5550000000402, "#1402");
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["note"] = "Llamar antes";
        }));

        var page = await client.GetFromJsonAsync<JsonObject>(ChangesRoute(flow.World.TenantId), Ct);
        var summary = await client.GetFromJsonAsync<JsonObject>($"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/summary", Ct);

        Assert.Equal(2, page!["total"]!.GetValue<int>());
        Assert.Equal(
            ["Blocked", "Pending"],
            page["items"]!.AsArray().Select(item => item!["status"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, summary!["changesToReview"]!.GetValue<int>());
    }

    [Fact]
    public async Task ChangesOfAnOrderComeNewestFirst()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited("2026-10-01T10:30:00-05:00"));
        await flow.DeliverAsync("orders/updated", "wh-3", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T11:00:00-05:00";
            order["note"] = "Llamar antes";
        }));
        using var client = flow.Client(OrdersPermissions.OrderRead);
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");

        var changes = await client.GetFromJsonAsync<JsonArray>($"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/{mirrorId}/changes", Ct);

        Assert.Equal(
            ["Pending", "Superseded"],
            changes!.Select(change => change!["status"]!.GetValue<string>()).ToArray());
        Assert.Equal("#1001", changes![0]!["orderName"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheChangeCarriesTheOrderCurrencyAndRawAmounts()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        using var client = flow.Client(OrdersPermissions.OrderRead);

        var page = await client.GetFromJsonAsync<JsonObject>(ChangesRoute(flow.World.TenantId), Ct);

        var change = page!["items"]![0]!;
        Assert.Equal("COP", change["currency"]!.GetValue<string>());
        var lines = change["groups"]!.AsArray().Single(group => group!["kind"]!.GetValue<string>() == "Lines")!;
        Assert.Equal("Apply", lines["outcome"]!.GetValue<string>());
        var row = lines["rows"]![0]!;
        Assert.Equal(System.Text.Json.JsonValueKind.Number, row["before"]!["unitPrice"]!.GetValueKind());
        Assert.Equal((2m, 1m), (row["before"]!["quantity"]!.GetValue<decimal>(), row["after"]!["quantity"]!.GetValue<decimal>()));
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

        public static async Task<Flow> StartAsync(Action<IServiceCollection>? configureServices = null)
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString())
            {
                ConfigureTestServices = services =>
                {
                    services.RemoveAll<IObjectStorage>();
                    services.AddSingleton<IObjectStorage>(new InMemoryObjectStorage());
                    configureServices?.Invoke(services);
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

        /// <summary>La próxima escritura de un cambio <c>Applied</c> falla en la base, una sola vez: es el guardado de Shopify que
        /// cae después de que Quotations ya confirmó el pedido. La secuencia no vuelve atrás con el rollback, así que no se rearma.</summary>
        public Task FailTheNextAppliedChangeAsync() =>
            ExecuteAsync(
                ConnectionString,
                """
                CREATE SEQUENCE public.fail_applied_change;
                CREATE FUNCTION public.fail_applied_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.status = 'Applied' AND nextval('public.fail_applied_change') = 1 THEN
                        RAISE EXCEPTION 'Simulated failure after the order was written.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fail_applied_change BEFORE INSERT OR UPDATE ON shopify.order_edits
                    FOR EACH ROW EXECUTE FUNCTION public.fail_applied_change();
                """);

        public async ValueTask DisposeAsync()
        {
            Webhooks.Dispose();
            World.Tenant.Seeder.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    /// <summary>Cuántas veces más falla la reacción de §6.3 antes de delegar en la real.</summary>
    internal sealed class ReactionFailures
    {
        public int Remaining;
    }

    /// <summary>La reacción real de §6.3, que falla las veces que diga <see cref="ReactionFailures"/>: corre después del detector
    /// y antes del guardado de la ingesta.</summary>
    internal sealed class FailingOrderChangeReaction(ShopifyOrderChanges inner, ReactionFailures failures) : IShopifyOrderChangeReaction
    {
        public Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken) =>
            Interlocked.Decrement(ref failures.Remaining) >= 0
                ? throw new InvalidOperationException("Simulated failure of the payment and cancellation reaction.")
                : inner.ReactAsync(change, cancellationToken);
    }
}
