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

    /// <summary>La cantidad ya editada (1) y, más tarde, otra nota: un <c>orders/updated</c> que no toca las líneas.</summary>
    private static JsonObject NoteEditedAfterTheQuantity() =>
        Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:45:00-05:00";
            order["line_items"]![0]!["current_quantity"] = 1;
            order["note"] = "Llamar antes";
        });

    private static Task<string> ProjectedQuantityAsync(Flow flow) =>
        flow.ScalarAsync("SELECT trim_scale((applied_projection -> 'lines' -> 0 ->> 'quantity')::numeric)::text FROM shopify.orders");

    // Review N-1: el guardado que marca el cambio aplicado falla y, antes del reintento, un snapshot más nuevo lo reemplaza. Las
    // líneas ya están en el pedido y el snapshot nuevo las conserva: el cambio reemplazado las aplicaba, así que la proyección las toma.
    [Fact]
    public async Task AnAutomaticApplySupersededBeforeItsSaveKeepsTheProjectionInSync()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.SetEditSyncAsync("Automatic");
        await flow.FailTheNextAppliedChangeAsync();
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());

        await flow.DeliverAsync("orders/updated", "wh-3", NoteEditedAfterTheQuantity());

        Assert.Equal("1", await ProjectedQuantityAsync(flow));
        await AssertTheNextQuantityEditAppliesAsync(flow);
    }

    // Review N-1, tienda manual: el aceptar falla después de Quotations y llega un orders/updated que no toca las líneas.
    [Fact]
    public async Task AFailedAcceptSupersededByAnotherUpdateKeepsTheProjectionInSync()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var accept = $"{ChangesRoute(flow.World.TenantId)}/{await flow.ScalarAsync("SELECT id::text FROM shopify.order_edits")}/accept";
        await flow.FailTheNextAppliedChangeAsync();
        Assert.False((await client.PostAsync(accept, content: null, Ct)).IsSuccessStatusCode);

        await flow.DeliverAsync("orders/updated", "wh-3", NoteEditedAfterTheQuantity());

        Assert.Equal("1", await ProjectedQuantityAsync(flow));
        await flow.SetEditSyncAsync("Automatic");
        await AssertTheNextQuantityEditAppliesAsync(flow);
    }

    // Review N-2: lo aplicado ya está en el pedido, pero antes del reintento el pedido dejó de ser editable. No hay nada que
    // escribir: el cambio queda aplicado (no bloqueado) y la proyección toma lo que el pedido tiene.
    [Fact]
    public async Task ARetryOnAnOrderThatIsNoLongerEditableSettlesInsteadOfBlocking()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.SetEditSyncAsync("Automatic");
        await flow.FailTheNextAppliedChangeAsync();
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var orderId = await flow.ScalarAsync("SELECT id::text FROM quotations.orders");
        Assert.True((await client.PostAsync(
            $"/api/v1/tenants/{flow.World.TenantId}/orders/{orderId}/prepare", content: null, Ct)).IsSuccessStatusCode);

        await flow.DeliverAsync("orders/updated", "wh-3", QuantityEdited());

        Assert.Equal("Applied", await flow.ScalarAsync("SELECT coalesce(string_agg(status, ','), '(none)') FROM shopify.order_edits"));
        Assert.Equal("1", await ProjectedQuantityAsync(flow));
    }

    // Review N-2: rechazar el cambio cuyo aceptar falló después de Quotations también deja la proyección con lo que el pedido tiene.
    [Fact]
    public async Task RejectingAChangeWhoseAcceptFailedKeepsTheProjectionInSync()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/updated", "wh-2", QuantityEdited());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var change = $"{ChangesRoute(flow.World.TenantId)}/{await flow.ScalarAsync("SELECT id::text FROM shopify.order_edits")}";
        await flow.FailTheNextAppliedChangeAsync();
        Assert.False((await client.PostAsync($"{change}/accept", content: null, Ct)).IsSuccessStatusCode);

        var rejected = await client.PostAsync($"{change}/reject", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("1", await ProjectedQuantityAsync(flow));
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

    /// <summary>La dirección como la devuelve Shopify después del <c>orderUpdate</c> de QEP (plan D12): el nombre de QEP en
    /// <c>first_name</c> (Shopify arma <c>name</c>), la dirección entera en <c>address1</c>, la ciudad y el departamento de QEP.</summary>
    private static JsonObject EchoOfTheEnvigadoCorrection(string updatedAt, string address1 = "Calle 10 # 43-20 Apto 301") =>
        Fixture(order =>
        {
            order["updated_at"] = updatedAt;
            var address = order["shipping_address"]!.AsObject();
            address["name"] = "Laura Gómez";
            address["first_name"] = "Laura Gómez";
            address["last_name"] = "";
            address["address1"] = address1;
            address["address2"] = "";
            address["city"] = "ENVIGADO";
            address["province"] = "ANTIOQUIA";
            address["province_code"] = "ANT";
        });

    // Spec 2026-11-14 §3 y §5 (anti-objetivo: el eco de una corrección de QEP no aparece como cambio). El asesor corrige la ciudad,
    // Shopify devuelve orders/updated con ella: no hay cambio y la versión aplicada adopta el snapshot. La proyección no se mueve
    // (plan D3), así que la edición siguiente de Shopify sobre la dirección es conflicto: gana lo del asesor (Review Focus 5).
    [Fact]
    public async Task TheEchoOfAPushedCorrectionCreatesNoChange()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await ShopifyOrderPushWorkerTests.CorrectTheCityToEnvigadoAsync(flow);
        var projection = await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders");

        await flow.DeliverAsync("orders/updated", "wh-2", EchoOfTheEnvigadoCorrection("2026-10-01T10:30:00-05:00"));

        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_edits"));
        Assert.Equal("ENVIGADO", await flow.ScalarAsync("SELECT applied_payload -> 'shippingAddress' ->> 'city' FROM shopify.orders"));
        Assert.Equal(projection, await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders"));

        await flow.DeliverAsync(
            "orders/updated", "wh-3", EchoOfTheEnvigadoCorrection("2026-10-01T11:00:00-05:00", address1: "Carrera 43A # 1-50"));

        Assert.Equal("Pending|ShippingAddress|Conflict|qep_changed", await flow.ScalarAsync(
            """
            SELECT e.status || '|' || (g ->> 'kind') || '|' || (g ->> 'outcome') || '|' || (g ->> 'reason')
            FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g
            """));
        Assert.Equal("Calle 10 # 43-20 Apto 301", await flow.ScalarAsync("SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"));
    }

    [Fact]
    public async Task AnImportedColombianOrderKeepsTheZipOnItsParty()
    {
        await using var flow = await Flow.StartAsync();

        await flow.DeliverAsync("orders/create", "wh-1", Fixture(order => order["shipping_address"]!["zip"] = "050021"));

        Assert.Equal(
            "050021|050021",
            await flow.ScalarAsync(
                """
                SELECT coalesce(p.postal_code, 'none') || '|' || coalesce(m.applied_projection -> 'shippingParty' ->> 'postalCode', 'none')
                FROM shopify.orders m
                JOIN quotations.order_parties p ON p.order_id = m.qep_order_id AND p.role = 'Shipping'
                """));
    }

    // Un pedido sin código postal (importado antes de que viajara) cuya corrección vuelve con el zip que Shopify conservaba: el zip
    // no es una novedad y el eco sigue sin crear un cambio.
    [Fact]
    public async Task AnEchoWithTheZipShopifyAlreadyHadCreatesNoChange()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture(order => order["shipping_address"]!["zip"] = "050021"));
        await ExecuteAsync(flow.ConnectionString, "UPDATE quotations.order_parties SET postal_code = NULL WHERE role = 'Shipping'");
        await ExecuteAsync(
            flow.ConnectionString,
            "UPDATE shopify.orders SET applied_projection = jsonb_set(applied_projection, '{shippingParty,postalCode}', 'null')");
        await ShopifyOrderPushWorkerTests.CorrectTheCityToEnvigadoAsync(flow);
        var echo = EchoOfTheEnvigadoCorrection("2026-10-01T10:30:00-05:00");
        echo["shipping_address"]!["zip"] = "050021";

        await flow.DeliverAsync("orders/updated", "wh-2", echo);

        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_edits"));
    }

    /// <summary>Un pedido como quedó guardado antes de que el espejo leyera el zip: los payloads no tienen la clave (distinto de
    /// <c>"zip": null</c>) y el pedido no tiene código postal.</summary>
    private static async Task<Flow> StartWithPayloadsWithoutZipAsync()
    {
        var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await ExecuteAsync(
            flow.ConnectionString,
            """
            UPDATE shopify.orders
            SET payload = payload #- '{shippingAddress,zip}', applied_payload = applied_payload #- '{shippingAddress,zip}'
            """);
        Assert.Equal("false|false", await flow.ScalarAsync(
            """
            SELECT (payload -> 'shippingAddress' ? 'zip')::text || '|' || (applied_payload -> 'shippingAddress' ? 'zip')::text
            FROM shopify.orders
            """));
        return flow;
    }

    // Sin la clave no se sabe qué zip tenía Shopify: el primero que llega no es un cambio.
    [Fact]
    public async Task AZipOverAPayloadWithoutTheKeyCreatesNoChange()
    {
        await using var flow = await StartWithPayloadsWithoutZipAsync();
        await flow.SetEditSyncAsync("Automatic");

        await flow.DeliverAsync("orders/updated", "wh-2", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["shipping_address"]!["zip"] = "050021";
        }));

        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_edits"));
        Assert.Equal("050021", await flow.ScalarAsync("SELECT applied_payload -> 'shippingAddress' ->> 'zip' FROM shopify.orders"));
        Assert.Equal("none", await flow.ScalarAsync(
            "SELECT coalesce(postal_code, 'none') FROM quotations.order_parties WHERE role = 'Shipping'"));
    }

    // La dirección que sí cambió se muestra sin la fila del zip.
    [Fact]
    public async Task AnAddressEditOverAPayloadWithoutTheKeyShowsNoZipRow()
    {
        await using var flow = await StartWithPayloadsWithoutZipAsync();

        await flow.DeliverAsync("orders/updated", "wh-2", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["shipping_address"]!["address1"] = "Carrera 43A # 1-50";
            order["shipping_address"]!["zip"] = "050021";
        }));

        Assert.Equal("address1", await flow.ScalarAsync(
            """
            SELECT string_agg(r ->> 'field', ',')
            FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g, jsonb_array_elements(g -> 'rows') AS r
            """));
    }

    // Un payload que sí guardó "zip": null: el zip que Shopify agrega después es un cambio de verdad.
    [Fact]
    public async Task AZipAddedOverARecordedNullIsAChange()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());

        await flow.DeliverAsync("orders/updated", "wh-2", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["shipping_address"]!["zip"] = "050021";
        }));

        Assert.Equal("Pending|zip", await flow.ScalarAsync(
            """
            SELECT e.status || '|' || (r ->> 'field')
            FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g, jsonb_array_elements(g -> 'rows') AS r
            """));
    }

    // «Editar pedido» puede dejar el pedido con una parte de fuera de Colombia: la sincronización lo sigue leyendo, y la edición
    // siguiente de Shopify sobre la dirección es conflicto (gana QEP).
    [Fact]
    public async Task AShopifyEditOnAnOrderWithASpanishPartyIsAConflict()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var saved = await ShopifyOrderDelivery.PutAsync(
            client, flow, isStorePickup: false,
            ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28")));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));

        await flow.DeliverAsync(
            "orders/updated", "wh-2", EchoOfTheEnvigadoCorrection("2026-10-01T10:30:00-05:00", address1: "Carrera 43A # 1-50"));

        Assert.Equal("Pending|ShippingAddress|Conflict|qep_changed", await flow.ScalarAsync(
            """
            SELECT coalesce(
                (SELECT e.status || '|' || (g ->> 'kind') || '|' || (g ->> 'outcome') || '|' || (g ->> 'reason')
                 FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g), 'none')
            """));
        Assert.Equal("Calle Mayor 1", await flow.ScalarAsync("SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"));
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
