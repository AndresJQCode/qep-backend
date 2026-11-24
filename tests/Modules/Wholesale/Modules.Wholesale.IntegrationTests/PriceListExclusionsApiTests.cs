using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;
using PriceListExclusionInput = Modules.Catalog.Application.PriceListExclusionInput;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// Spec 2026-11-20: las exclusiones de una lista contra Postgres. Filtradas por nombre (Testcontainers): la lista y su detalle,
/// el selector de productos y lo que ve un distribuidor aceptado (árbol, catálogo con total verdadero y 422 al pedir lo oculto).
/// </summary>
public sealed class PriceListExclusionsApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private const string Password = "Distri-2026!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    private async Task<Guid> ProductOfAsync(Guid variantId) =>
        (await WholesaleApiHarness.RowsAsync<Guid>(
            Factory, $"""SELECT product_id AS "Value" FROM catalog.product_variants WHERE id = {variantId}"""))[0];

    [Fact]
    public async Task ExclusionsAreSavedReturnedWithNamesClearedAndCountedInTheList()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Ferretería");
        var pot = await ProductOfAsync(await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Olla", 10_000m, home));

        var created = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(tenant.TenantId),
            new
            {
                name = "Oro",
                adjustmentPercent = -10m,
                visibility = "AllActive",
                categoryIds = Array.Empty<Guid>(),
                exclusions = new[]
                {
                    new { kind = "Product", id = pot },
                    new { kind = "Category", id = tools },
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = (await created.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal(
            [
                new PriceListExclusionDto("Category", tools, "Ferretería", null, null),
                new PriceListExclusionDto("Product", pot, "Olla", "H-1", "Hogar"),
            ],
            detail.Exclusions);
        var listed = (await manager.GetFromJsonAsync<PriceListsResponse>(WholesaleApiHarness.PriceListsUrl(tenant.TenantId), Ct))!;
        Assert.Equal((1, 1), (listed.Items[0].HiddenCategoryCount, listed.Items[0].HiddenProductCount));

        var cleared = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"{WholesaleApiHarness.PriceListsUrl(tenant.TenantId)}/{detail.Id}")
                {
                    Content = JsonContent.Create(new { name = "Oro", adjustmentPercent = -10m, visibility = "AllActive", categoryIds = Array.Empty<Guid>() }),
                },
                detail.Version),
            Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = (await cleared.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal((2L, 0), (after.Version, after.Exclusions!.Count));
    }

    [Fact]
    public async Task AnUnknownDestinationIs422OnItsIndex()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(tenant.TenantId),
            new
            {
                name = "Oro",
                adjustmentPercent = -10m,
                visibility = "AllActive",
                categoryIds = Array.Empty<Guid>(),
                exclusions = new[] { new { kind = "Product", id = Guid.CreateVersion7() } },
            },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await WholesaleApiHarness.BodyAsync(response));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Exclusions[0]", out _));
    }

    [Fact]
    public async Task ThePreviewReflectsUnsavedExclusionsAndCountsWhatTheyHide()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Cojín", 5_000m, home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "K-1", "Olla", 10_000m, kitchen);
        var loose = await ProductOfAsync(await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "S-1", "Suelto", 1_000m));

        var response = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PreviewUrl(tenant.TenantId),
            new
            {
                adjustmentPercent = -10m,
                visibility = "AllActive",
                categoryIds = Array.Empty<Guid>(),
                exclusions = new[] { new { kind = "Category", id = kitchen }, new { kind = "Product", id = loose } },
            },
            Ct);

        var preview = (await response.Content.ReadFromJsonAsync<PriceListPreviewDto>(Ct))!;
        Assert.Equal(["Cojín"], preview.Items.Select(item => item.Name));
        Assert.Equal((1, 2), (preview.TotalProducts, preview.HiddenProducts));
    }

    [Fact]
    public async Task TheProductPickerFiltersSearchesAndCountsProducts()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Cocina", home);
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Ferretería");
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "K-1", "Olla", 10_000m, kitchen);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Cojín", 5_000m, home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "F-1", "Martillo", 30_000m, tools);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "U-1", "Sólo dólares", priceCop: null);
        var url = $"/api/v1/tenants/{tenant.TenantId}/wholesale/products";

        var all = (await manager.GetFromJsonAsync<WholesaleProductsResponse>(url, Ct))!;
        var byCategory = (await manager.GetFromJsonAsync<WholesaleProductsResponse>($"{url}?categoryId={home}", Ct))!;
        var byText = (await manager.GetFromJsonAsync<WholesaleProductsResponse>($"{url}?search=mart", Ct))!;
        var paged = (await manager.GetFromJsonAsync<WholesaleProductsResponse>($"{url}?page=2&pageSize=2", Ct))!;
        using var reader = WholesaleApiHarness.CreateReader(Factory, tenant);

        Assert.Equal(["Cojín", "Martillo", "Olla"], all.Items.Select(item => item.Name));
        Assert.Equal(3, all.Total);
        Assert.Equal(["Cojín", "Olla"], byCategory.Items.Select(item => item.Name));
        Assert.Equal(("Martillo", "F-1", "Ferretería", 1), (byText.Items[0].Name, byText.Items[0].Code, byText.Items[0].CategoryName, byText.Items[0].VariantCount));
        Assert.Equal(["Olla"], paged.Items.Select(item => item.Name));
        Assert.Equal((3, 2, 2), (paged.Total, paged.Page, paged.PageSize));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task ADistributorSeesTheTrimmedTreeAndCatalogAndCannotOrderWhatIsHidden()
    {
        var wholesaler = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, wholesaler.TenantId, "Hogar");
        var kitchen = await WholesaleApiHarness.CreateCategoryAsync(manager, wholesaler.TenantId, "Cocina", home);
        var pots = await WholesaleApiHarness.CreateCategoryAsync(manager, wholesaler.TenantId, "Ollas", kitchen);
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, wholesaler.TenantId, "Ferretería");
        var cushion = await WholesaleApiHarness.CreateVariantAsync(manager, wholesaler.TenantId, "H-1", "Cojín", 5_000m, home);
        var pot = await WholesaleApiHarness.CreateVariantAsync(manager, wholesaler.TenantId, "P-1", "Olla", 10_000m, pots);
        var hammer = await WholesaleApiHarness.CreateVariantAsync(manager, wholesaler.TenantId, "F-1", "Martillo", 30_000m, tools);
        var saw = await WholesaleApiHarness.CreateVariantAsync(manager, wholesaler.TenantId, "F-2", "Serrucho", 20_000m, tools);
        var sawProduct = await ProductOfAsync(saw);
        var list = (await (await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(wholesaler.TenantId),
            new
            {
                name = "Con ocultos",
                adjustmentPercent = -10m,
                visibility = "Categories",
                categoryIds = new[] { home, tools },
                exclusions = new[] { new { kind = "Category", id = kitchen }, new { kind = "Product", id = sawProduct } },
            },
            Ct)).Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, wholesaler.TenantId, "Mayorista Central SAS");
        var email = $"distri-{Guid.NewGuid():N}@distri.co";
        var (distributor, token) = await WholesaleApiHarness.InviteAsync(Factory, manager, wholesaler, list.Id, company, email);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using (var session = WholesaleApiHarness.CreateSession(Factory, userId))
        {
            (await session.PostAsJsonAsync($"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct))
                .EnsureSuccessStatusCode();
        }

        var buyer = (await WholesaleApiHarness.RowsAsync<Guid>(
            Factory,
            $"""SELECT distributor_tenant_id AS "Value" FROM wholesale.distributors WHERE id = {distributor.Id}"""))[0];
        await using var scope = Factory.Services.CreateAsyncScope();
        var offers = scope.ServiceProvider.GetRequiredService<WholesaleOffers>();

        var tree = await offers.ListVisibleCategoriesAsync(wholesaler.TenantId, buyer, Ct);
        var page = await offers.SearchAsync(wholesaler.TenantId, buyer, null, null, 1, 50, Ct);
        var hiddenCategory = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            offers.SearchAsync(wholesaler.TenantId, buyer, null, kitchen, 1, 50, Ct));
        var hiddenByCategory = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            offers.PriceVisibleAsync(wholesaler.TenantId, buyer, [pot], Ct));
        var hiddenByProduct = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            offers.PriceVisibleAsync(wholesaler.TenantId, buyer, [saw], Ct));
        var (_, visible) = await offers.PriceVisibleAsync(wholesaler.TenantId, buyer, [cushion, hammer], Ct);

        Assert.Equal(new[] { home, tools }.Order(), tree.Select(node => node.Id).Order());
        Assert.Equal(["Cojín", "Martillo"], page.Items.Select(item => item.Name).Order());
        Assert.Equal(2, page.Total);
        Assert.Equal(WholesaleErrorCodes.CategoryNotVisible, hiddenCategory.Code);
        Assert.Equal(WholesaleErrorCodes.ProductNotVisible, hiddenByCategory.Code);
        Assert.Equal(WholesaleErrorCodes.ProductNotVisible, hiddenByProduct.Code);
        Assert.Equal(2, visible.Count);
    }

    // Regla 4 del spec: una categoría excluida no toca a un producto sin categoría; sólo se oculta excluyéndolo a él.
    [Fact]
    public async Task AProductWithoutCategoryStaysVisibleUnderAnExcludedCategoryAndFallsOnlyByItsOwnId()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var home = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Hogar");
        var tools = await WholesaleApiHarness.CreateCategoryAsync(manager, tenant.TenantId, "Ferretería");
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "H-1", "Cojín", 5_000m, home);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "F-1", "Martillo", 30_000m, tools);
        var loose = await ProductOfAsync(await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "S-1", "Suelto", 1_000m));
        await using var scope = Factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IWholesaleCatalogReader>();

        var (underCategory, categoryTotal) = await reader.SearchSellableAsync(
            tenant.TenantId, "COP", null, [home], null, null, 1, 50, Ct);
        var (sample, sampleTotal) = await reader.SampleAsync(tenant.TenantId, "COP", null, [home], null, 20, Ct);
        var (byId, idTotal) = await reader.SearchSellableAsync(tenant.TenantId, "COP", null, [home], [loose], null, 1, 50, Ct);

        Assert.Equal(["Martillo", "Suelto"], underCategory.Select(item => item.ProductName).Order());
        Assert.Equal(2, categoryTotal);
        Assert.Equal((2, 2), (sample.Count, sampleTotal));
        Assert.Equal(["Martillo"], byId.Select(item => item.ProductName));
        Assert.Equal(1, idTotal);
    }

    [Fact]
    public async Task ThePickerIsOrderedByNameAcrossPagesAlsoWhenSearching()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        // El más corto es el más parecido a «olla» (manda la relevancia), pero el último por nombre.
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "O-1", "Olla zzz", 1_000m);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "O-2", "Olla mmm con un nombre bastante largo", 1_000m);
        await WholesaleApiHarness.CreateVariantAsync(manager, tenant.TenantId, "O-3", "Olla aaa con un nombre todavía más largo que el anterior", 1_000m);
        var url = $"/api/v1/tenants/{tenant.TenantId}/wholesale/products?search=olla&pageSize=1";

        var names = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = (await manager.GetFromJsonAsync<WholesaleProductsResponse>($"{url}&page={page}", Ct))!;
            names.Add(Assert.Single(result.Items).Name);
        }

        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public async Task ACategoryOrProductOfAnotherTenantIsRefusedOnCreateAndUpdateAndNeverShownInTheDetail()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var other = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, mine);
        using var otherManager = WholesaleApiHarness.CreateManager(Factory, other);
        var foreignCategory = await WholesaleApiHarness.CreateCategoryAsync(otherManager, other.TenantId, "Ajena");
        var foreignProduct = await ProductOfAsync(
            await WholesaleApiHarness.CreateVariantAsync(otherManager, other.TenantId, "X-1", "Producto ajeno", 1_000m));
        var exclusions = new[] { new { kind = "Category", id = foreignCategory }, new { kind = "Product", id = foreignProduct } };

        var created = await manager.PostAsJsonAsync(
            WholesaleApiHarness.PriceListsUrl(mine.TenantId),
            new { name = "Oro", adjustmentPercent = -10m, visibility = "AllActive", categoryIds = Array.Empty<Guid>(), exclusions },
            Ct);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, mine.TenantId, "Plata", 5m);
        var updated = await manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"{WholesaleApiHarness.PriceListsUrl(mine.TenantId)}/{list.Id}")
                {
                    Content = JsonContent.Create(new
                    {
                        name = "Plata",
                        adjustmentPercent = -5m,
                        visibility = "AllActive",
                        categoryIds = Array.Empty<Guid>(),
                        exclusions,
                    }),
                },
                list.Version),
            Ct);

        foreach (var response in new[] { created, updated })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var body = JsonDocument.Parse(await WholesaleApiHarness.BodyAsync(response));
            var errors = body.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("Exclusions[0]", out _));
            Assert.True(errors.TryGetProperty("Exclusions[1]", out _));
        }

        var detail = (await manager.GetFromJsonAsync<PriceListDetailDto>(
            $"{WholesaleApiHarness.PriceListsUrl(mine.TenantId)}/{list.Id}", Ct))!;
        Assert.Empty(detail.Exclusions!);
        Assert.Equal(1L, detail.Version);
    }
}
