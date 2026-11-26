using System.Net;
using System.Net.Http.Json;
using Modules.Catalog.Application;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal, D-5): la caja cobra con la lista asignada al canal POS, de punta a punta: la lista
/// se crea y se asigna por la API de Catalog, y el lookup real de Bootstrapper la aplica contra Postgres. Una lista de otro canal
/// no la toca.
/// </summary>
public sealed class PosPriceListApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRegisterPricesWithThePosListAndIgnoresTheStoreOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var lists = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, CatalogPermissions.PriceListManage);
        var catalogUrl = $"/api/v1/tenants/{world.Tenant.TenantId}/catalog";

        var before = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=SH-400", Ct);
        var store = await CreateListAsync(lists, catalogUrl, "Tienda", 50m);
        await AssignAsync(lists, catalogUrl, "Store", store);
        var withStoreList = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=SH-400", Ct);
        var pos = await CreateListAsync(lists, catalogUrl, "Caja", 10m);
        await AssignAsync(lists, catalogUrl, "Pos", pos);
        var withPosList = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=SH-400", Ct);

        Assert.Equal(11_900m, before!.UnitPrice);
        Assert.Equal(11_900m, withStoreList!.UnitPrice);
        // 11 900 × 1,1 = 13 090 (COP sin decimales).
        Assert.Equal(13_090m, withPosList!.UnitPrice);
    }

    private static async Task<Guid> CreateListAsync(HttpClient client, string catalogUrl, string name, decimal adjustmentPercent)
    {
        var response = await client.PostAsJsonAsync(
            $"{catalogUrl}/price-lists", new { name, adjustmentPercent, visibility = "AllActive" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!.Id;
    }

    private static async Task AssignAsync(HttpClient client, string catalogUrl, string channel, Guid priceListId)
    {
        var response = await client.PutAsJsonAsync(
            $"{catalogUrl}/channel-prices", new { channels = new[] { new { channel, priceListId = (Guid?)priceListId } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
