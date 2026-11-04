using System.Text.Json.Nodes;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// El pedido que sale de «Tomar» en la pantalla de pedidos: su detalle trae la solicitud con el teléfono del visitante, y el listado
/// lo filtra por origen. Necesitan Docker.
/// </summary>
public sealed class StoreOrderDetailApiTests
{
    private static readonly object NewCustomerBody = new { newCustomer = new { classificationId = (Guid?)null } };

    [Fact]
    public async Task AStoreOrderDetailCarriesTheRequestPhone()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);

        var storeRequest = order["storeRequest"]!;
        var state = await rig.StateAsync(request);
        Assert.Equal(request, (Guid)storeRequest["id"]!);
        Assert.Equal("T-0001", (string)storeRequest["number"]!);
        Assert.Equal((Guid)taken["customerId"]!, (Guid)storeRequest["customerId"]!);
        Assert.Equal("Ana Pérez", (string)storeRequest["customerName"]!);
        Assert.Equal("3001234567", (string)storeRequest["phone"]!);
        Assert.Equal("+573001234567", (string)storeRequest["phoneE164"]!);
        Assert.Equal("ana@test.co", (string)storeRequest["email"]!);
        Assert.Equal(state.Total, (decimal)storeRequest["displayedTotal"]!);
        Assert.Equal("COP", (string)storeRequest["currency"]!);
        Assert.Equal(state.TotalDifference, (decimal?)storeRequest["totalDifference"]);
    }

    [Fact]
    public async Task TheOrdersListFiltersByStoreSource()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        var orderId = (Guid)(await rig.TakeOkAsync(request, NewCustomerBody))["orderId"]!;

        var store = await ListAsync(rig, "Store");
        var manual = await ListAsync(rig, "Manual");

        var item = Assert.Single(store["items"]!.AsArray())!;
        Assert.Equal(orderId, (Guid)item["id"]!);
        Assert.Equal("Store", (string)item["source"]!);
        Assert.Empty(manual["items"]!.AsArray());
    }

    private static async Task<JsonNode> ListAsync(StoreInboxRig rig, string source)
    {
        var response = await rig.Inbox.GetAsync(
            $"/api/v1/tenants/{TenantId}/orders?source={source}", TestContext.Current.CancellationToken);
        var json = await PublicStoreFixture.JsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        return json;
    }
}
