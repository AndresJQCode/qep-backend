using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Quotations.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>Un pedido de Shopify de España nace con su parte de envío y toma las ediciones de esa dirección.</summary>
public sealed class ShopifyForeignShippingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Spanish(string address1, string updatedAt)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        order["updated_at"] = updatedAt;
        var address = order["shipping_address"]!;
        address["address1"] = address1;
        address["address2"] = null;
        address["city"] = "Madrid";
        address["province"] = "Madrid";
        address["province_code"] = "M";
        address["country"] = "Spain";
        address["country_code"] = "ES";
        address["zip"] = "28013";
        return order;
    }

    [Fact]
    public async Task ASpanishOrderIsBornWithItsShippingPartyAndTakesAddressEdits()
    {
        await using var flow = await Flow.StartAsync();
        await flow.SetEditSyncAsync("Automatic");

        await flow.DeliverAsync("orders/create", "wh-1", Spanish("Calle Mayor 1", "2026-10-01T10:00:00-05:00"));

        var regionId = await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28");
        using var client = flow.Client(OrdersPermissions.OrderRead);
        var route = $"/api/v1/tenants/{flow.World.TenantId}/orders/{await ShopifyOrderDelivery.OrderIdAsync(flow)}";
        var detail = (await client.GetFromJsonAsync<JsonObject>(route, Ct))!;
        var party = detail["order"]!["parties"]!.AsArray().Single(node => node!["role"]!.GetValue<string>() == "Shipping")!;
        Assert.Equal(
            ("ES", "Madrid", regionId, "28013"),
            (party["country"]!.GetValue<string>(), party["cityName"]!.GetValue<string>(),
                party["region"]!["id"]!.GetValue<Guid>(), party["postalCode"]!.GetValue<string>()));

        await flow.DeliverAsync("orders/updated", "wh-2", Spanish("Calle de Alcalá 20", "2026-10-01T10:30:00-05:00"));

        Assert.Equal("Applied", await flow.ScalarAsync(
            "SELECT coalesce(string_agg(status, ','), 'none') FROM shopify.order_edits"));
        Assert.StartsWith(
            "Calle de Alcalá 20",
            await flow.ScalarAsync("SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"),
            StringComparison.Ordinal);
    }
}
