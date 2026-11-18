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

    [Fact]
    public async Task ThePageSizeIsCappedAt200()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);

        var page = await Handler().HandleAsync(
            new ListSupplierCatalogQuery(TenantId, PurchaseOrderTestData.SupplierId, null, 0, 500), Ct);

        Assert.Equal((1, 200), (page.Page, page.PageSize));
    }
}
