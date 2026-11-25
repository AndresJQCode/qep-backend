using BuildingBlocks.Application;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>Mayorista fase 3 (Task 6): el catálogo del mayorista para armar una orden, con el costo que la orden guardaría.</summary>
public sealed class SupplierCatalogTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;
    private static readonly Guid Wholesaler = PurchaseOrderTestData.SupplierTenantId;

    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakeWholesaleCatalog _wholesale = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ListSupplierCatalogHandler Handler(params string[] permissions) =>
        new(new FakeExecutionContext(TenantId, permissions.Length == 0 ? [PurchasesPermissions.OrderManage] : permissions),
            _suppliers, _wholesale);

    [Fact]
    public async Task CatalogOfANonLinkedSupplierIs422()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var error = await Assert.ThrowsAsync<PurchasesDomainException>(() =>
            Handler().HandleAsync(new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct));

        Assert.Equal(PurchasesErrorCodes.SupplierNotLinked, error.Code);
        Assert.Empty(_wholesale.Searches);
    }

    [Fact]
    public async Task AnotherTenantsSupplierIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler().HandleAsync(new ListSupplierCatalogQuery(TenantId, Guid.CreateVersion7(), null, 1, 50), Ct));

        Assert.Equal("suppliers.supplier.not_found", error.Code);
    }

    [Fact]
    public async Task TheCatalogNeedsOrderManage()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(PurchasesPermissions.OrderRead)
                .HandleAsync(new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task CatalogItemsCarryTheNetUnitCost()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var item = _wholesale.Add("ACE-1", 14000m, 15m, 11900m, 19m);

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, " aceite ", 2, 0), Ct);

        Assert.Equal(("USD", 1, 2, 50), (page.Currency, page.Total, page.Page, page.PageSize));
        Assert.Equal(new SupplierCatalogPriceListDto("Distribuidor estándar", 15m), page.PriceList);
        Assert.Equal(
            new SupplierCatalogItemDto(item.VariantId, "ACE-1", "Producto ACE-1", 14000m, 15m, 11900m, 19m, 10000m),
            Assert.Single(page.Items));
        Assert.Equal([(TenantId, Wholesaler, (string?)"aceite", 2, 50)], _wholesale.Searches);
    }

    // Spec 2026-11-19: el ítem sigue plano por variante pero trae el producto y la foto para agrupar y dibujar tarjetas.
    [Fact]
    public async Task CatalogItemsCarryTheProductAndPhoto()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var productId = Guid.CreateVersion7();
        var medium = _wholesale.Add("CAM-M", 14000m, 15m, 11900m, 19m);
        var large = _wholesale.Add("CAM-L", 14000m, 15m, 11900m, 19m);
        _wholesale.Items[medium.VariantId] = medium with
        {
            ProductId = productId,
            ProductName = "Camiseta",
            ImageUrl = "https://cdn.example.co/m.png",
        };
        _wholesale.Items[large.VariantId] = large with { ProductId = productId, ProductName = "Camiseta", ImageUrl = null };

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct);

        Assert.Equal(
            [(productId, "Camiseta", (string?)"https://cdn.example.co/m.png"), (productId, "Camiseta", null)],
            page.Items.Select(item => (item.ProductId, item.ProductName, item.ImageUrl)));
    }

    [Fact]
    public async Task ThePageSizeIsCappedAt200()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 0, 500), Ct);

        Assert.Equal((1, 200), (page.Page, page.PageSize));
    }

    // Spec 2026-11-19: con la lista oculta del mayorista, el BFF no manda montos pero sí lo que identifica al producto.
    [Fact]
    public async Task ACatalogOfAHiddenPriceListHasNoAmounts()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var item = _wholesale.Add("ACE-1", 14000m, 15m, 11900m, 19m);
        _wholesale.Items[item.VariantId] = item with
        {
            ProductId = Guid.CreateVersion7(),
            ProductName = "Aceite",
            ImageUrl = "https://cdn.example.co/a.png",
        };
        _wholesale.ShowPrices = false;

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct);

        Assert.False(page.ShowPrices);
        Assert.Equal(new SupplierCatalogPriceListDto("Distribuidor estándar", null), page.PriceList);
        var shown = Assert.Single(page.Items);
        Assert.Equal((null, null, null, null), (shown.BasePrice, shown.DiscountPercent, shown.ListPrice, shown.UnitCost));
        Assert.Equal(
            (item.VariantId, "ACE-1", "Producto ACE-1", 19m, "Aceite", "https://cdn.example.co/a.png"),
            (shown.VariantId, shown.Code, shown.Name, shown.TaxRatePercent, shown.ProductName, shown.ImageUrl));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task ACatalogOfAVisiblePriceListKeepsItsAmounts()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var item = _wholesale.Add("ACE-1", 14000m, 15m, 11900m, 19m);
        _wholesale.ShowPrices = true;

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct);

        Assert.True(page.ShowPrices);
        Assert.Equal(new SupplierCatalogPriceListDto("Distribuidor estándar", 15m), page.PriceList);
        Assert.Equal(
            new SupplierCatalogItemDto(item.VariantId, "ACE-1", "Producto ACE-1", 14000m, 15m, 11900m, 19m, 10000m),
            Assert.Single(page.Items));
    }

    private ListSupplierCatalogCategoriesHandler CategoriesHandler(params string[] permissions) =>
        new(new FakeExecutionContext(TenantId, permissions.Length == 0 ? [PurchasesPermissions.OrderManage] : permissions),
            _suppliers, _wholesale);

    private static ListSupplierCatalogCategoriesQuery CategoriesQuery(Guid supplierId) => new(TenantId, supplierId);

    [Fact]
    public async Task CategoriesOfANonLinkedSupplierIs422()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var error = await Assert.ThrowsAsync<PurchasesDomainException>(() =>
            CategoriesHandler().HandleAsync(CategoriesQuery(PurchaseOrderTestData.SupplierId), Ct));

        Assert.Equal(PurchasesErrorCodes.SupplierNotLinked, error.Code);
    }

    [Fact]
    public async Task CategoriesOfAnotherTenantsSupplierIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            CategoriesHandler().HandleAsync(CategoriesQuery(Guid.CreateVersion7()), Ct));

        Assert.Equal("suppliers.supplier.not_found", error.Code);
    }

    [Fact]
    public async Task CategoriesNeedOrderManage()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            CategoriesHandler(PurchasesPermissions.OrderRead)
                .HandleAsync(CategoriesQuery(PurchaseOrderTestData.SupplierId), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task CategoriesWithAnInactiveRelationshipIs403()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        _wholesale.Forbidden = true;

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            CategoriesHandler().HandleAsync(CategoriesQuery(PurchaseOrderTestData.SupplierId), Ct));

        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task CategoriesComeInTheOrderTheWholesalerGives()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var zeta = new WholesaleCategory(Guid.CreateVersion7(), "Zeta", null);
        var alfa = new WholesaleCategory(Guid.CreateVersion7(), "Alfa", zeta.Id);
        _wholesale.Categories.AddRange([zeta, alfa]);

        var result = await CategoriesHandler().HandleAsync(CategoriesQuery(PurchaseOrderTestData.SupplierId), Ct);

        Assert.Equal(
            [new SupplierCatalogCategoryDto(zeta.Id, "Zeta", null), new SupplierCatalogCategoryDto(alfa.Id, "Alfa", zeta.Id)],
            result.Items);
    }

    [Fact]
    public async Task CatalogPassesTheCategoryIdToTheWholesaleCatalog()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
        var categoryId = Guid.CreateVersion7();

        await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50, categoryId), Ct);

        Assert.Equal([(Guid?)categoryId], _wholesale.SearchCategoryIds);
    }

    [Fact]
    public async Task CatalogWithoutCategoryIdPassesNull()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);

        await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 1, 50), Ct);

        Assert.Equal([(Guid?)null], _wholesale.SearchCategoryIds);
    }
}
