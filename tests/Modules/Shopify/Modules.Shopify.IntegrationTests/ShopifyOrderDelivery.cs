using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>«Editar pedido» sobre el pedido de Shopify que el flujo importó (el único), por las rutas de Quotations: el cuerpo repite
/// lo guardado y sólo cambia la entrega.</summary>
internal static class ShopifyOrderDelivery
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<string> OrderIdAsync(Flow flow) => flow.ScalarAsync("SELECT qep_order_id::text FROM shopify.orders");

    public static async Task<Guid> RegionIdAsync(Flow flow, string country, string code) =>
        Guid.Parse(await flow.ScalarAsync(
            $"SELECT id::text FROM geography.regions WHERE country_code = '{country}' AND code = '{code}'"));

    public static JsonObject SpanishParty(Guid? regionId, string? postalCode = "28001") =>
        new()
        {
            ["name"] = "Lucía Fernández",
            ["phone"] = "+34600111222",
            ["email"] = "entregas@madrid.es",
            ["address"] = "Calle Mayor 1",
            ["country"] = "ES",
            ["cityName"] = "Madrid",
            ["regionId"] = regionId,
            ["postalCode"] = postalCode,
        };

    public static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, Flow flow, bool isStorePickup, JsonObject? shipping) =>
        await PutAsync(client, flow.World.TenantId, await OrderIdAsync(flow), isStorePickup, shipping);

    /// <summary>El mismo guardado sobre un pedido dado, para los mundos que no son un <c>Flow</c> o tienen más de un
    /// pedido.</summary>
    public static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, string orderId, bool isStorePickup, JsonObject? shipping)
    {
        var (route, version, body) = await BodyAsync(client, tenantId, orderId, isStorePickup, shipping);
        var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Ct);
    }

    public static async Task<HttpResponseMessage> PreviewAsync(
        HttpClient client, Flow flow, bool isStorePickup, JsonObject? shipping)
    {
        var (route, _, body) = await BodyAsync(client, flow.World.TenantId, await OrderIdAsync(flow), isStorePickup, shipping);
        return await client.PostAsJsonAsync($"{route}/preview", body, Ct);
    }

    private static async Task<(string Route, long Version, JsonObject Body)> BodyAsync(
        HttpClient client, Guid tenantId, string orderId, bool isStorePickup, JsonObject? shipping)
    {
        var route = $"/api/v1/tenants/{tenantId}/orders/{orderId}";
        var detail = (await client.GetFromJsonAsync<JsonObject>(route, Ct))!;
        var order = detail["order"]!;
        var body = new JsonObject
        {
            ["items"] = new JsonArray(
                [.. detail["items"]!.AsArray().Select(item => (JsonNode)new JsonObject
                {
                    ["variantId"] = item!["variantId"]!.DeepClone(),
                    ["quantity"] = item["quantity"]!.DeepClone(),
                })]),
            ["notes"] = order["notes"]?.DeepClone(),
            ["globalScaleFloor"] = order["globalScaleFloor"]?.DeepClone(),
            ["isRetail"] = order["isRetail"]!.DeepClone(),
            ["delivery"] = new JsonObject
            {
                ["isStorePickup"] = isStorePickup,
                ["shipping"] = shipping?.DeepClone(),
            },
        };
        return (route, order["version"]!.GetValue<long>(), body);
    }
}
