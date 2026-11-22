using System.Net;
using System.Net.Http.Json;
using Modules.Catalog.Application;
using Modules.Wholesale.Application;
using PriceListExclusionInput = Modules.Catalog.Application.PriceListExclusionInput;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>Las rutas de listas de precios contra Postgres (contrato «Listas de precios»).</summary>
public sealed class PriceListApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task CreateReturns201AndTheDetail()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(tenant.TenantId),
            new { name = "  Oro  ", adjustmentPercent = -12.5m, visibility = "Categories", categoryIds = new[] { home } },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal($"/api/v1/tenants/{tenant.TenantId}/catalog/price-lists/{detail.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("Oro", -12.5m, "Categories", 0, 1L), (detail.Name, detail.AdjustmentPercent, detail.Visibility, detail.DistributorCount, detail.Version));
        Assert.Equal([(home, "Hogar")], detail.Categories.Select(category => (category.Id, category.Name)));

        var listed = await manager.GetFromJsonAsync<PriceListsResponse>(WholesaleApiHarness.PriceListsUrl(tenant.TenantId), Ct);
        var row = Assert.Single(listed!.Items);
        Assert.Equal((detail.Id, 1, "Hogar"), (row.Id, row.CategoryCount, row.Categories[0].Name));
    }

    [Fact]
    public async Task DuplicateNameIs409NameTaken()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(tenant.TenantId),
            new { name = "ORO", adjustmentPercent = -5m, visibility = "AllActive", categoryIds = Array.Empty<Guid>() },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("catalog.price_list.name_taken", await WholesaleApiHarness.CodeOfAsync(response));
    }

    [Fact]
    public async Task DeleteInUseIs409InUse()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);

        var response = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Delete, $"{WholesaleApiHarness.PriceListsUrl(tenant.TenantId)}/{list.Id}"),
                list.Version),
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("catalog.price_list.in_use", await WholesaleApiHarness.CodeOfAsync(response));

        var unused = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Plata", 5m);
        var deleted = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Delete, $"{WholesaleApiHarness.PriceListsUrl(tenant.TenantId)}/{unused.Id}"),
                unused.Version),
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task StaleIfMatchIs412AndMissingIs428()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var url = $"{WholesaleApiHarness.PriceListsUrl(tenant.TenantId)}/{list.Id}";
        var body = new { name = "Oro 2", adjustmentPercent = -11m, visibility = "AllActive", categoryIds = Array.Empty<Guid>() };

        var missing = await manager.PutAsJsonAsync(url, body, Ct);
        var stale = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) }, list.Version + 1),
            Ct);
        var fresh = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) }, list.Version),
            Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", await WholesaleApiHarness.CodeOfAsync(missing));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await WholesaleApiHarness.CodeOfAsync(stale));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var updated = (await fresh.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal(("Oro 2", list.Version + 1), (updated.Name, updated.Version));
    }

    // D-10 y Review Focus 5: COP sin decimales (regla por defecto del tenant) y sin la variante que no tiene precio en COP.
    [Fact]
    public async Task PreviewRoundsWithTheTenantRuleAndOmitsUnpricedVariants()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var priced = await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "P-1", "Olla", 10_333m);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "P-2", "Tetera", priceCop: null);

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PreviewUrl(tenant.TenantId),
            new { adjustmentPercent = -10m, visibility = "AllActive", categoryIds = Array.Empty<Guid>() },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<PriceListPreviewDto>(Ct))!;
        Assert.Equal("COP", preview.Currency);
        var item = Assert.Single(preview.Items);
        // 10 333 × 0,9 = 9 299,7 → 9 300 con la regla de COP (cero decimales, al más cercano).
        Assert.Equal((priced, "P-1", 10_333m, 9_300m), (item.VariantId, item.Code, item.BasePrice, item.ListPrice));
    }

    // Ruling W5: el árbol de categorías para la visibilidad, sin catalog.product.read.
    [Fact]
    public async Task CategoriesAreReadableWithOnlyThePriceListPermission()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        // Spec 2026-11-22: más wholesale.distributor.read, que es lo que cierra la ruta sin el módulo mayorista.
        using var listsOnly = WholesaleApiHarness.CreateClient(
            Factory, tenant.SubjectId, tenant.TenantId, WholesalePermissions.PriceListManage, WholesalePermissions.DistributorRead);
        using var readerOnly = WholesaleApiHarness.CreateReader(Factory, tenant);

        var categories = await listsOnly.GetFromJsonAsync<WholesaleCategoriesResponse>(
            WholesaleApiHarness.CategoriesUrl(tenant.TenantId), Ct);
        var forbidden = await readerOnly.GetAsync(WholesaleApiHarness.CategoriesUrl(tenant.TenantId), Ct);

        Assert.Equal(
            [(kitchen, "Cocina", (Guid?)home), (home, "Hogar", (Guid?)null)],
            categories!.Items.Select(category => (category.Id, category.Name, category.ParentId)));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // Spec 2026-11-19: showPrices es opcional; un PUT que no lo trae (un cliente viejo) no vuelve a mostrar una lista oculta.
    [Fact]
    public async Task PutWithoutShowPricesKeepsHiddenList()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var created = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(tenant.TenantId),
            new { name = "Oculta", adjustmentPercent = -10m, visibility = "AllActive", categoryIds = Array.Empty<Guid>(), showPrices = false },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = (await created.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.False(list.ShowPrices);
        var url = $"{WholesaleApiHarness.PriceListsUrl(tenant.TenantId)}/{list.Id}";

        var put = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = JsonContent.Create(new { name = "Oculta 2", adjustmentPercent = -12m, visibility = "AllActive", categoryIds = Array.Empty<Guid>() }),
                },
                list.Version),
            Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await put.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!.ShowPrices);
        var read = (await manager.GetFromJsonAsync<PriceListDetailDto>(url, Ct))!;
        Assert.Equal(("Oculta 2", -12m, false), (read.Name, read.AdjustmentPercent, read.ShowPrices));
        var listed = await manager.GetFromJsonAsync<PriceListsResponse>(WholesaleApiHarness.PriceListsUrl(tenant.TenantId), Ct);
        Assert.False(Assert.Single(listed!.Items).ShowPrices);

        var shown = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = JsonContent.Create(new { name = "Oculta 2", adjustmentPercent = -12m, visibility = "AllActive", categoryIds = Array.Empty<Guid>(), showPrices = true }),
                },
                read.Version),
            Ct);
        Assert.True((await shown.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!.ShowPrices);
    }
}
