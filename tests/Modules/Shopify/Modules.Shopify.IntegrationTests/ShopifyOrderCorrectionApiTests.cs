using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Audit.Domain;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
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
