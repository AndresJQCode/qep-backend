using System.Net;
using System.Net.Http.Json;
using Modules.Catalog.Application;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal, D-5): una línea de cotización nace al precio de la lista del canal Quotations; la
/// de la tienda no la toca. La lista se crea y se asigna por la API de Catalog y el lookup real de Bootstrapper la aplica.
/// </summary>
public sealed class QuotationPriceListApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQuotationLineIsPricedWithTheQuotationsList()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        using var lists = CreateClient(factory, ownerUserId.ToString(), tenantId.ToString(), CatalogPermissions.PriceListManage);
        var catalogUrl = $"/api/v1/tenants/{tenantId}/catalog";
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var store = await CreateListAsync(lists, catalogUrl, "Tienda", 50m);
        var quotations = await CreateListAsync(lists, catalogUrl, "Asesores", -10m);
        var assigned = await lists.PutAsJsonAsync(
            $"{catalogUrl}/channel-prices",
            new { channels = new object[] { new { channel = "Store", priceListId = (Guid?)store }, new { channel = "Quotations", priceListId = (Guid?)quotations } } },
            Ct);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var created = await CreateQuotationAsync(client, tenantId, clientId);

        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}/items", new AddQuotationItemRequest(productId, 1m), Ct);

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var quotation = (await added.Content.ReadFromJsonAsync<QuotationResponse>(Ct))!;
        Assert.Equal(90_000m, Assert.Single(quotation.Items).UnitPrice);
    }

    private static async Task<Guid> CreateListAsync(HttpClient client, string catalogUrl, string name, decimal adjustmentPercent)
    {
        var response = await client.PostAsJsonAsync(
            $"{catalogUrl}/price-lists", new { name, adjustmentPercent, visibility = "AllActive" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!.Id;
    }
}
