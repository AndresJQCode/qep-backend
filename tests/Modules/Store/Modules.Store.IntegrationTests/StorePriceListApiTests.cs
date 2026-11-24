using System.Net;
using System.Net.Http.Json;
using Modules.Catalog.Application;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal, D-5): la tienda pública muestra y cotiza con la lista del canal Store. El catálogo
/// público pasa por <c>StoreCatalogReader</c> y el carrito por Quotations con el canal Store: los dos tienen que dar el mismo precio,
/// y un precio fijo gana sobre el ajuste.
/// </summary>
public sealed class StorePriceListApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCatalogAndTheCartUseTheStoreList()
    {
        await using var fixture = await OpenAsync();
        var (_, adjusted) = await fixture.CreateProductAsync("Polo", "P-1", Prices(10_000m));
        var (_, fixedOne) = await fixture.CreateProductAsync("Gorra", "G-1", Prices(20_000m));
        using var lists = fixture.Settings.ClientFor(StoreApiHarness.TenantId, CatalogPermissions.PriceListManage);
        var catalogUrl = $"/api/v1/tenants/{Tenant}/catalog";

        var created = await lists.PostAsJsonAsync(
            $"{catalogUrl}/price-lists", new { name = "Tienda", adjustmentPercent = 20m, visibility = "AllActive" }, Ct);
        var list = (await created.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        var overrides = new HttpRequestMessage(HttpMethod.Put, $"{catalogUrl}/price-lists/{list.Id}/overrides")
        {
            Content = JsonContent.Create(new { set = new[] { new { variantId = fixedOne, currency = "COP", amount = 18_500m } } }),
        };
        overrides.Headers.TryAddWithoutValidation("If-Match", $"\"{list.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await lists.SendAsync(overrides, Ct)).StatusCode);
        var assigned = await lists.PutAsJsonAsync(
            $"{catalogUrl}/channel-prices", new { channels = new[] { new { channel = "Store", priceListId = (Guid?)list.Id } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var page = await fixture.GetJsonAsync("/products?currency=COP");
        var quote = await JsonAsync(await fixture.QuoteAsync(
            new { currency = "COP", lines = new[] { new { variantId = adjusted, quantity = 1 }, new { variantId = fixedOne, quantity = 1 } } }));

        var prices = page["items"]!.AsArray().ToDictionary(item => (string)item!["name"]!, item => (decimal)item!["priceFrom"]!);
        Assert.Equal(12_000m, prices["Polo"]);
        Assert.Equal(18_500m, prices["Gorra"]);
        Assert.Equal(30_500m, (decimal)quote["total"]!);
    }
}
