using System.Net;
using System.Net.Http.Json;
using Modules.Geography.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using CalliShop = Modules.Shopify.IntegrationTests.ShopifyCityCorrectionFlowTests.CalliShop;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Review SC-10/11: el impacto de confirmar contra un pedido y un cliente reales, por el endpoint. Vive en el proyecto de Shopify
/// porque es el que sabe armar un pedido «CALLI» importado por el webhook (la prueba de Geography no puede sembrar Quotations ni
/// Customers); ejerce <c>ReadOrdersAsync</c> y <c>ReadCustomersAsync</c> de punta a punta, que la prueba de Geography nunca toca.
/// </summary>
public sealed class CityOperatorImpactFlowTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheImpactOfResolvingReadsARealPendingOrderAndAnIncompleteCustomer()
    {
        await using var shop = await CalliShop.StartAsync(operatorTenantId: OperatorTenantId);
        // La fila que reportó la importación de hoy: su ocurrencia lleva el pedido real y el cliente que quedó incompleto sólo por
        // la ciudad. El sinónimo que dejó CalliShop no estorba: el impacto no mira si ya existe uno.
        var rowId = await ScalarAsync<Guid>(
            shop.ConnectionString, "SELECT id FROM geography.unresolved_cities WHERE normalized_text = 'CALLI'");
        using var client = CreateClient(
            shop.Factory, OperatorSubjectId, OperatorTenantId,
            GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);
        var route = $"/api/v1/tenants/{OperatorTenantId}/operator/cities/unresolved/{rowId}/impact";

        var baseline = await ImpactAsync(client, route, shop.CaliId);

        Assert.Equal(1, baseline.OrdersToFix.Count);
        Assert.Equal(shop.World.TenantId, Assert.Single(baseline.OrdersToFix.ByTenant).TenantId);
        Assert.False(string.IsNullOrWhiteSpace(baseline.OrdersToFix.ByTenant[0].Name));
        Assert.Equal(1, baseline.CustomersToComplete.Count);
        Assert.Equal((0, 0, 0, 0), (
            baseline.OrdersMissingData.Count, baseline.OrdersWithShipping.Count, baseline.OrdersNotPending.Count,
            baseline.CustomersStayIncomplete.Count));
        Assert.Equal("CALI", baseline.City.Name);

        // El pedido se aprueba y alguien edita al cliente: el pedido pasa a la lista de los que no se tocan, con su número y su
        // estado; el cliente (que sólo necesitaba la ciudad) ya no está en ningún grupo.
        await ExecuteAsync(
            shop.ConnectionString, "UPDATE quotations.orders SET status = 'Approved' WHERE id = @order", ("order", shop.OrderId));
        await ExecuteAsync(
            shop.ConnectionString, "UPDATE customers.customers SET version = version + 1 WHERE id = @customer", ("customer", shop.CustomerId));

        var changed = await ImpactAsync(client, route, shop.CaliId);

        Assert.Equal((0, 0), (changed.OrdersToFix.Count, changed.CustomersToComplete.Count));
        Assert.Equal(0, changed.CustomersStayIncomplete.Count);
        var notPending = Assert.Single(changed.OrdersNotPending.Orders);
        Assert.Equal((shop.World.TenantId, shop.OrderId, "Approved"), (notPending.TenantId, notPending.OrderId, notPending.Status));
        Assert.False(string.IsNullOrWhiteSpace(notPending.OrderNumber));
        // El impacto no escribe: la fila sigue Pending y sin cambios.
        Assert.Equal("Pending|1", await ScalarAsync<string>(
            shop.ConnectionString, "SELECT status || '|' || version FROM geography.unresolved_cities WHERE id = @id", ("id", rowId)));
    }

    private static async Task<ResolveImpactDto> ImpactAsync(HttpClient client, string route, Guid cityId)
    {
        var response = await client.PostAsJsonAsync(route, new { cityId }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResolveImpactDto>(Ct))!;
    }
}
