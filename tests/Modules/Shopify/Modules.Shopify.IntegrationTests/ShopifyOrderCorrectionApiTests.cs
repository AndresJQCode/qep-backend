using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Audit.Domain;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-14 §5 de punta a punta: <c>PUT …/shopify/orders/{id}/corrections</c> escribe el pedido de QEP (Quotations enrolado
/// en la transacción de Shopify) y encola el envío en <c>shopify.order_pushes</c> en la misma transacción; si algo falla después de
/// escribir el pedido, no queda ni lo uno ni lo otro.
/// </summary>
public sealed class ShopifyOrderCorrectionApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Fixture() => (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;

    private static string CorrectionsRoute(Flow flow, string mirrorId) =>
        $"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/{mirrorId}/corrections";

    private static async Task<(Guid DepartmentId, Guid CityId)> EnvigadoAsync(Flow flow)
    {
        var ids = (await flow.ScalarAsync("SELECT department_id::text || '|' || id::text FROM geography.cities WHERE divipola_code = '05266'"))
            .Split('|');
        return (Guid.Parse(ids[0]), Guid.Parse(ids[1]));
    }

    private static async Task<JsonObject> CorrectionBodyAsync(Flow flow, bool withParty = true, string? buyerNotes = null, JsonArray? attributes = null)
    {
        var (departmentId, cityId) = await EnvigadoAsync(flow);
        return new JsonObject
        {
            ["shippingParty"] = withParty
                ? new JsonObject
                {
                    ["name"] = "Laura Gómez",
                    ["phone"] = "+573001112233",
                    ["email"] = "laura.gomez@example.com",
                    ["address"] = "Calle 10 # 43-20 Apto 301",
                    ["departmentId"] = departmentId,
                    ["cityId"] = cityId,
                }
                : null,
            ["buyerNotes"] = buyerNotes,
            ["attributes"] = attributes ?? new JsonArray(
                new JsonObject { ["key"] = "documento", ["value"] = "1036000111" },
                new JsonObject { ["key"] = "tipo_documento", ["value"] = "CC" }),
        };
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Flow flow, JsonObject body, bool ifMatch = true)
    {
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");
        var request = new HttpRequestMessage(HttpMethod.Put, CorrectionsRoute(flow, mirrorId)) { Content = JsonContent.Create(body) };
        if (ifMatch)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{await flow.ScalarAsync("SELECT version::text FROM quotations.orders")}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task CorrectingTheCityWritesTheOrderAndEnqueuesAPush()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var projection = await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders");

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, envigado) = await EnvigadoAsync(flow);
        Assert.Equal(envigado.ToString(), await flow.ScalarAsync("SELECT city_id::text FROM quotations.order_parties WHERE role = 'Shipping'"));
        Assert.Equal("1|Pending|{ShippingAddress}", await flow.ScalarAsync(
            "SELECT count(*) || '|' || max(status) || '|' || max(groups::text) FROM shopify.order_pushes"));
        Assert.Equal(projection, await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders"));
        var dto = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Pending", dto!["push"]!["status"]!.GetValue<string>());
        Assert.Equal(
            "1",
            await flow.ScalarAsync("SELECT count(*)::text FROM quotations.order_history WHERE event_type = 'PartiesChanged' AND member_id IS NOT NULL"));
        Assert.Equal("1", await flow.ScalarAsync("SELECT count(*)::text FROM audit.entries WHERE action = 'shopify.order.corrected'"));
    }

    // Spec 2026-11-24 §5: la corrección del panel no recibe código postal, así que al reescribir la parte lo conserva; si no,
    // cada corrección lo borraría. El código postal se siembra a mano: la fixture no trae zip.
    [Fact]
    public async Task ACorrectionKeepsThePostalCode()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        await flow.ScalarAsync(
            "UPDATE quotations.order_parties SET postal_code = '055422' WHERE role = 'Shipping' RETURNING postal_code");

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, envigado) = await EnvigadoAsync(flow);
        Assert.Equal(
            $"{envigado}|055422",
            await flow.ScalarAsync(
                "SELECT city_id::text || '|' || coalesce(postal_code, '') FROM quotations.order_parties WHERE role = 'Shipping'"));
    }

    // El código postal es de la provincia: al cambiar de departamento, el que tenía el pedido sería el de otra dirección. Se
    // borra, y el envío a Shopify no lleva ninguno (el zip del pedido de Shopify es de la provincia anterior).
    [Fact]
    public async Task ACorrectionToAnotherDepartmentDropsThePostalCode()
    {
        var shopify = new ShopifyOrderPushWorkerTests.RecordingUpdater();
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        var order = Fixture();
        order["shipping_address"]!["zip"] = "050021";
        await flow.DeliverAsync("orders/create", "wh-1", order);
        Assert.Equal("050021", await flow.ScalarAsync("SELECT postal_code FROM quotations.order_parties WHERE role = 'Shipping'"));
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var bogota = (await flow.ScalarAsync("SELECT department_id::text || '|' || id::text FROM geography.cities WHERE divipola_code = '11001'"))
            .Split('|');
        var body = await CorrectionBodyAsync(flow);
        body["shippingParty"]!["departmentId"] = bogota[0];
        body["shippingParty"]!["cityId"] = bogota[1];

        var response = await PutAsync(client, flow, body);
        await ActivatorUtilities.CreateInstance<ShopifyOrderPushWorker>(flow.Factory.Services).DrainAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"{bogota[1]}|none",
            await flow.ScalarAsync(
                "SELECT city_id::text || '|' || coalesce(postal_code, 'none') FROM quotations.order_parties WHERE role = 'Shipping'"));
        var address = Assert.Single(shopify.Updates).ShippingAddress!;
        Assert.Equal(("DC", (string?)null), (address.ProvinceCode, address.Zip));
    }

    // El formulario de la corrección no tiene código postal: reenviar la misma parte de un pedido que lo guarda no es un cambio.
    [Fact]
    public async Task ResendingThePartyOfAnOrderWithPostalCodeSendsNothing()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        await flow.ScalarAsync(
            "UPDATE quotations.order_parties SET postal_code = '055422' WHERE role = 'Shipping' RETURNING postal_code");
        var body = await CorrectionBodyAsync(flow);
        var stored = (await flow.ScalarAsync(
            "SELECT department_id::text || '|' || city_id::text FROM quotations.order_parties WHERE role = 'Shipping'")).Split('|');
        body["shippingParty"]!["departmentId"] = stored[0];
        body["shippingParty"]!["cityId"] = stored[1];

        var response = await PutAsync(client, flow, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("NothingToSend", dto!["shopifySync"]?.GetValue<string>());
        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_pushes"));
    }

    // Owner, 2026-11-14: con la conexión operativa, la respuesta dice que el cambio quedó encolado para Shopify.
    [Fact]
    public async Task ACorrectionOnAnActiveConnectionSaysItWasQueued()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Queued", dto!["shopifySync"]?.GetValue<string>());
        Assert.Equal("1", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_pushes"));
    }

    /// <summary>Pausa o reanuda la conexión de la tienda del flujo, como lo haría Integrations.</summary>
    internal static async Task SetConnectionActiveAsync(Flow flow, bool active)
    {
        await using var scope = flow.Factory.Services.CreateAsyncScope();
        var connectionId = Guid.Parse(await flow.ScalarAsync("SELECT connection_id::text FROM shopify.orders LIMIT 1"));
        var connection = await scope.ServiceProvider.GetRequiredService<Modules.Integrations.Application.IIntegrationConnectionRepository>()
            .FindAsync(flow.World.TenantId, connectionId, Ct);
        Assert.NotNull(connection);
        if (active)
        {
            connection.Resume(DateTimeOffset.UtcNow);
        }
        else
        {
            connection.Pause(DateTimeOffset.UtcNow);
        }

        await scope.ServiceProvider.GetRequiredService<Modules.Integrations.Application.IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
    }

    // Owner, 2026-11-14 (reemplaza review B3 M1): con la conexión pausada, la corrección se guarda en QEP y el envío queda retenido
    // (Held, connection_inactive): la marca de que hay algo por sincronizar cuando se reactive. La respuesta lo dice para que la
    // pantalla avise.
    [Fact]
    public async Task ACorrectionOnAnInactiveConnectionIsSavedAndHeld()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await SetConnectionActiveAsync(flow, active: false);
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Llamar antes", await flow.ScalarAsync("SELECT buyer_notes FROM quotations.orders"));
        Assert.Equal("1|Held|connection_inactive|{Note}", await flow.ScalarAsync(
            "SELECT count(*) || '|' || max(status) || '|' || max(failure_code) || '|' || max(groups::text) FROM shopify.order_pushes"));
        var dto = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Held", dto!["shopifySync"]?.GetValue<string>());
        Assert.Equal("Held", dto["push"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASecondCorrectionMergesIntoTheHeldPush()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await SetConnectionActiveAsync(flow, active: false);
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"))).StatusCode);

        var response = await PutAsync(
            client,
            flow,
            await CorrectionBodyAsync(
                flow, withParty: false, buyerNotes: "Llamar antes", attributes: new JsonArray(new JsonObject { ["key"] = "documento", ["value"] = "999" })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1|Held|{Note,Attributes}", await flow.ScalarAsync(
            "SELECT count(*) || '|' || max(status) || '|' || max(groups::text) FROM shopify.order_pushes"));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();

    private static string ReleaseOrderRoute(Flow flow, string mirrorId) =>
        $"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/{mirrorId}/push";

    // Owner, 2026-11-14: reactivada la conexión, «Sincronizar con Shopify» de un pedido suelta su envío retenido.
    [Fact]
    public async Task ReleasingAHeldPushOfAnOrderMakesItPending()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await SetConnectionActiveAsync(flow, active: false);
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"))).StatusCode);
        await SetConnectionActiveAsync(flow, active: true);
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");

        var response = await client.PostAsync(ReleaseOrderRoute(flow, mirrorId), content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending|0||true", await flow.ScalarAsync(
            "SELECT status || '|' || attempts || '|' || coalesce(failure_code, '') || '|' || (lease_until IS NULL)::text FROM shopify.order_pushes"));
        var dto = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Pending", dto!["push"]!["status"]!.GetValue<string>());
        Assert.Equal("1", await flow.ScalarAsync("SELECT count(*)::text FROM audit.entries WHERE action = 'shopify.order.push_released'"));
    }

    [Fact]
    public async Task ReleasingWhileTheConnectionIsStillInactiveIs422()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await SetConnectionActiveAsync(flow, active: false);
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"))).StatusCode);
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");

        var response = await client.PostAsync(ReleaseOrderRoute(flow, mirrorId), content: null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("shopify.store.not_operational", await CodeAsync(response));
        Assert.Equal("Held", await flow.ScalarAsync("SELECT status FROM shopify.order_pushes"));
    }

    [Fact]
    public async Task ReleasingAnOrderWithNothingHeldIs409()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes"))).StatusCode);
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");

        var response = await client.PostAsync(ReleaseOrderRoute(flow, mirrorId), content: null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("shopify.push.nothing_held", await CodeAsync(response));
    }

    private static JsonObject OtherOrder(long id, string name)
    {
        var order = Fixture();
        order["id"] = id;
        order["admin_graphql_api_id"] = $"gid://shopify/Order/{id}";
        order["name"] = name;
        return order;
    }

    // Owner, 2026-11-14: el botón global de la tienda suelta todos sus envíos retenidos en una sentencia, sólo de esa conexión y
    // ese tenant, y dice cuántos.
    [Fact]
    public async Task ReleasingTheHeldPushesOfAStoreCountsOnlyItsOwn()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await flow.DeliverAsync("orders/create", "wh-2", OtherOrder(820982911946154600, "#1002"));
        await flow.DeliverAsync("orders/create", "wh-3", OtherOrder(820982911946154601, "#1003"));
        await SetConnectionActiveAsync(flow, active: false);
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        foreach (var mirrorId in (await flow.ScalarAsync("SELECT string_agg(id::text, ',' ORDER BY id) FROM shopify.orders")).Split(','))
        {
            var request = new HttpRequestMessage(HttpMethod.Put, CorrectionsRoute(flow, mirrorId))
            {
                Content = JsonContent.Create(await CorrectionBodyAsync(flow, withParty: false, buyerNotes: "Llamar antes")),
            };
            var orderVersion = await ScalarAsync<string>(
                flow.ConnectionString,
                "SELECT o.version::text FROM quotations.orders o JOIN shopify.orders m ON m.qep_order_id = o.id WHERE m.id = @id",
                ("id", Guid.Parse(mirrorId)));
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{orderVersion}\"");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, Ct)).StatusCode);
        }

        // Uno pasa a otro tenant y otro a otra conexión del mismo tenant: el botón no los toca.
        await ExecuteAsync(flow.ConnectionString, """
            UPDATE shopify.order_pushes SET tenant_id = gen_random_uuid()
             WHERE mirror_order_id = (SELECT id FROM shopify.orders WHERE order_name = '#1002');
            UPDATE shopify.order_pushes SET connection_id = gen_random_uuid()
             WHERE mirror_order_id = (SELECT id FROM shopify.orders WHERE order_name = '#1003');
            """);
        await SetConnectionActiveAsync(flow, active: true);
        var connectionId = await flow.ScalarAsync("SELECT connection_id::text FROM shopify.orders LIMIT 1");
        using var stores = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage, ShopifyPermissions.ConnectionRead);
        var listed = await stores.GetFromJsonAsync<JsonArray>($"/api/v1/tenants/{flow.World.TenantId}/shopify/stores", Ct);
        Assert.Equal(1, listed!.Single(store => store!["connectionId"]!.GetValue<string>() == connectionId)!["heldPushCount"]!.GetValue<int>());

        var response = await stores.PostAsync(
            $"/api/v1/tenants/{flow.World.TenantId}/shopify/stores/{connectionId}/pushes/release", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(1, result!["released"]!.GetValue<int>());
        Assert.Equal("Held,Held,Pending", await flow.ScalarAsync("SELECT string_agg(status, ',' ORDER BY status) FROM shopify.order_pushes"));
    }

    [Fact]
    public async Task AShopifyFailureAfterTheOrderWriteRollsBackBoth()
    {
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyAuditRecorder>();
            services.AddScoped<IShopifyAuditRecorder, FailingCorrectionAudit>();
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var city = await flow.ScalarAsync("SELECT city_id::text FROM quotations.order_parties WHERE role = 'Shipping'");

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(city, await flow.ScalarAsync("SELECT city_id::text FROM quotations.order_parties WHERE role = 'Shipping'"));
        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_pushes"));
        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM quotations.order_history WHERE event_type = 'PartiesChanged'"));
    }

    [Fact]
    public async Task CorrectingANotEditableOrderIs422AndEnqueuesNothing()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var orderId = await flow.ScalarAsync("SELECT id::text FROM quotations.orders");
        Assert.True((await client.PostAsync(
            $"/api/v1/tenants/{flow.World.TenantId}/orders/{orderId}/prepare", content: null, Ct)).IsSuccessStatusCode);

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_pushes"));
    }

    [Fact]
    public async Task CorrectingWithoutIfMatchIs428()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

        var response = await PutAsync(client, flow, await CorrectionBodyAsync(flow), ifMatch: false);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("precondition.if_match_required", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnInvalidCorrectionReturnsTheContractErrorKeys()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var body = await CorrectionBodyAsync(flow, attributes: new JsonArray(new JsonObject { ["key"] = "", ["value"] = "x" }));
        body["shippingParty"]!["name"] = "";

        var response = await PutAsync(client, flow, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("validation.failed", problem!["code"]!.GetValue<string>());
        var errors = problem["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("shippingParty.name"));
        Assert.True(errors.ContainsKey("attributes[0].key"));
    }

    // Owner 2026-11-15: la ciudad tiene que existir y ser del departamento elegido, y el departamento tiene que existir; si no, 422
    // con la clave del campo y nada escrito ni encolado. Antes una ciudad inexistente dejaba el envío reintentando ~8 h y una de
    // otro departamento mandaba a Shopify una dirección incoherente.
    [Theory]
    [InlineData("otherDepartmentCity", "shippingParty.cityId")]
    [InlineData("unknownCity", "shippingParty.cityId")]
    [InlineData("unknownDepartment", "shippingParty.departmentId")]
    public async Task AShippingCityOutsideItsDepartmentIs422OnItsKey(string scenario, string expectedKey)
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var city = await flow.ScalarAsync("SELECT city_id::text FROM quotations.order_parties WHERE role = 'Shipping'");
        var body = await CorrectionBodyAsync(flow);
        var party = body["shippingParty"]!;
        switch (scenario)
        {
            case "otherDepartmentCity":
                // Bogotá (11001) es de Bogotá D.C., no de Antioquia (el departamento de Envigado).
                party["cityId"] = Guid.Parse(await flow.ScalarAsync("SELECT id::text FROM geography.cities WHERE divipola_code = '11001'"));
                break;
            case "unknownCity":
                party["cityId"] = Guid.CreateVersion7();
                break;
            default:
                party["departmentId"] = Guid.CreateVersion7();
                break;
        }

        var response = await PutAsync(client, flow, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("validation.failed", problem!["code"]!.GetValue<string>());
        Assert.Equal([expectedKey], problem["errors"]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(city, await flow.ScalarAsync("SELECT city_id::text FROM quotations.order_parties WHERE role = 'Shipping'"));
        Assert.Equal("0", await flow.ScalarAsync("SELECT count(*)::text FROM shopify.order_pushes"));
    }

    // Review Minor 4: un null en la lista de atributos es un 422 con su índice, no un 500.
    [Fact]
    public async Task ANullAttributeIs422WithItsIndex()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var body = await CorrectionBodyAsync(
            flow, withParty: false, attributes: new JsonArray(new JsonObject { ["key"] = "documento", ["value"] = "1" }, null));

        var response = await PutAsync(client, flow, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("validation.failed", problem!["code"]!.GetValue<string>());
        Assert.Equal(["attributes[1]"], problem["errors"]!.AsObject().Select(pair => pair.Key).ToArray());
    }

    [Fact]
    public async Task GetSyncShowsAttributesAndThePendingPush()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var attributes = new JsonArray(
            new JsonObject { ["key"] = "documento", ["value"] = "1036000222" },
            new JsonObject { ["key"] = "tipo_documento", ["value"] = "CC" });
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, flow, await CorrectionBodyAsync(flow, withParty: false, attributes: attributes))).StatusCode);
        var qepOrderId = await flow.ScalarAsync("SELECT id::text FROM quotations.orders");

        var sync = await client.GetFromJsonAsync<JsonObject>($"/api/v1/tenants/{flow.World.TenantId}/shopify/qep-orders/{qepOrderId}", Ct);

        Assert.Equal("Pending", sync!["push"]!["status"]!.GetValue<string>());
        Assert.Equal(["Attributes"], sync["push"]!["groups"]!.AsArray().Select(group => group!.GetValue<string>()).ToArray());
        Assert.True(sync["attributesEditedInQep"]!.GetValue<bool>());
        Assert.True(sync["canEdit"]!.GetValue<bool>());
        Assert.Equal("1036000222", sync["attributes"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == "documento")!["value"]!.GetValue<string>());
    }

    /// <summary>La auditoría de Shopify que falla justo en la corrección: el pedido de QEP ya se escribió en la transacción, y el
    /// rollback tiene que llevárselo junto con el envío.</summary>
    private sealed class FailingCorrectionAudit : IShopifyAuditRecorder
    {
        public void Record(
            Guid tenantId,
            Guid actorId,
            AuditActorType actorType,
            string action,
            string resourceType,
            string resourceId,
            string changedFieldsJson,
            DateTimeOffset occurredAt)
        {
            if (string.Equals(action, ShopifyAuditActions.OrderCorrected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure of the Shopify side after the order was written.");
            }
        }
    }
}
