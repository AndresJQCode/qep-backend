using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Modules.Quotations.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-24 §11.1.4: «Editar pedido» sobre un pedido de Shopify rechaza, al guardar y en el cálculo previo, una dirección de
/// envío que el worker no podría mandar. El código postal nunca es motivo (owner, 2026-11-25). Un pedido de otro origen no pasa por
/// estas reglas (<c>OrderEditsApiTests</c>).
/// </summary>
public sealed class ShopifyOrderDeliveryEditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Fixture(Action<JsonObject>? edit = null)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        edit?.Invoke(order);
        return order;
    }

    private static HttpClient Client(Flow flow) => flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"Expected 422, got {response.StatusCode}: {text}");
        Assert.Equal(code, JsonNode.Parse(text)!["code"]!.GetValue<string>());
    }

    private static Task<string> StoredPartyAsync(Flow flow) =>
        flow.ScalarAsync(
            """
            SELECT coalesce(
                (SELECT coalesce(country, 'CO') || '|' || address || '|' || coalesce(postal_code, 'none')
                 FROM quotations.order_parties WHERE role = 'Shipping'), 'none')
            """);

    /// <summary><c>versión|recoger|historial|partes de envío|eventos de dirección|envíos a Shopify</c>.</summary>
    private static Task<string> StoredOrderAsync(Flow flow) =>
        flow.ScalarAsync(
            """
            SELECT o.version
                || '|' || o.is_store_pickup
                || '|' || (SELECT count(*) FROM quotations.order_history h WHERE h.order_id = o.id)
                || '|' || (SELECT count(*) FROM quotations.order_parties p WHERE p.order_id = o.id AND p.role = 'Shipping')
                || '|' || (SELECT count(*) FROM platform.outbox_messages
                           WHERE event_name = 'quotations.order.shipping-party-changed.v1')
                || '|' || (SELECT count(*) FROM shopify.order_pushes)
            FROM quotations.orders o
            """);

    [Fact]
    public async Task ShopifyCustomerAddressIsRejected()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = Client(flow);
        var before = await StoredPartyAsync(flow);

        var response = await ShopifyOrderDelivery.PutAsync(client, flow, isStorePickup: false, shipping: null);

        await AssertProblemAsync(response, "order.delivery.shopify_party_required");
        Assert.Equal(before, await StoredPartyAsync(flow));
    }

    // La parte no cambia (no la tenía): un guardado que repite la entrega de un pedido sin parte no se rechaza.
    [Fact]
    public async Task ResendingTheCustomerAddressOfAShopifyOrderWithoutPartyIsAccepted()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture(order => order["shipping_address"]!["city"] = "Pueblo Perdido"));
        Assert.Equal("none", await StoredPartyAsync(flow));
        using var client = Client(flow);

        var response = await ShopifyOrderDelivery.PutAsync(client, flow, isStorePickup: false, shipping: null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    // De recoger a enviar con los datos del cliente: no hay parte propia que mandar, pero la entrega cambió y Shopify se quedaría con
    // la dirección anterior. Se rechaza en el cálculo previo y al guardar, sin escribir nada.
    [Fact]
    public async Task SendingAPickupShopifyOrderToTheCustomerAddressIsRejected()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = Client(flow);
        var pickup = await ShopifyOrderDelivery.PutAsync(client, flow, isStorePickup: true, shipping: null);
        Assert.True(pickup.StatusCode == HttpStatusCode.OK, await pickup.Content.ReadAsStringAsync(Ct));
        var before = await StoredOrderAsync(flow);

        var preview = await ShopifyOrderDelivery.PreviewAsync(client, flow, isStorePickup: false, shipping: null);
        var save = await ShopifyOrderDelivery.PutAsync(client, flow, isStorePickup: false, shipping: null);

        await AssertProblemAsync(preview, "order.delivery.shopify_party_required");
        await AssertProblemAsync(save, "order.delivery.shopify_party_required");
        Assert.Equal(before, await StoredOrderAsync(flow));
    }

    [Fact]
    public async Task ShopifySpainWithoutRegionIsRejected()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = Client(flow);
        var before = await StoredPartyAsync(flow);

        var response = await ShopifyOrderDelivery.PutAsync(
            client, flow, isStorePickup: false, ShopifyOrderDelivery.SpanishParty(regionId: null));

        await AssertProblemAsync(response, "order.delivery.shopify_region_required");
        Assert.Equal(before, await StoredPartyAsync(flow));
    }

    // Owner P-1: sin código postal y en otra provincia que la del pedido de Shopify, el guardado se acepta.
    [Fact]
    public async Task ShopifySpainWithoutPostalCodeIsAccepted()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = Client(flow);

        var response = await ShopifyOrderDelivery.PutAsync(
            client, flow, isStorePickup: false,
            ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28"), postalCode: null));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ES|Calle Mayor 1|none", await StoredPartyAsync(flow));
    }

    [Fact]
    public async Task ThePreviewAppliesTheSameShopifyRules()
    {
        await using var flow = await Flow.StartAsync();
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        using var client = Client(flow);

        var customerAddress = await ShopifyOrderDelivery.PreviewAsync(client, flow, isStorePickup: false, shipping: null);
        var withoutRegion = await ShopifyOrderDelivery.PreviewAsync(
            client, flow, isStorePickup: false, ShopifyOrderDelivery.SpanishParty(regionId: null));
        var pickup = await ShopifyOrderDelivery.PreviewAsync(client, flow, isStorePickup: true, shipping: null);

        await AssertProblemAsync(customerAddress, "order.delivery.shopify_party_required");
        await AssertProblemAsync(withoutRegion, "order.delivery.shopify_region_required");
        Assert.True(pickup.StatusCode == HttpStatusCode.OK, await pickup.Content.ReadAsStringAsync(Ct));
    }
}
