using BuildingBlocks.Application;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

public sealed class PurchaseOrderLineBuilderTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;

    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakeCatalogLookup _catalog = new();

    [Fact]
    public async Task TheRulesRunInTheContractOrder()
    {
        var inactive = Guid.CreateVersion7();
        _suppliers.Add(inactive, "Inactivo", isActive: false);
        var variant = _catalog.Add("SKU-1", "Camiseta", 19m);

        var currencyFirst = await Assert.ThrowsAsync<PurchasesDomainException>(
            () => Build(inactive, "XYZ", [new PurchaseOrderLineInput(variant.VariantId, 1m, 1m)]));
        var supplierBeforeEmpty = await Assert.ThrowsAsync<PurchasesDomainException>(
            () => Build(inactive, "COP", []));

        Assert.Equal(PurchasesErrorCodes.InvalidCurrency, currencyFirst.Code);
        Assert.Equal(PurchasesErrorCodes.SupplierInactive, supplierBeforeEmpty.Code);
    }

    [Fact]
    public async Task AMissingSupplierIsTheSuppliersNotFound()
    {
        var notFound = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => Build(Guid.CreateVersion7(), "COP", []));

        Assert.Equal("suppliers.supplier.not_found", notFound.Code);
    }

    [Fact]
    public async Task EmptyLinesComeBeforeTheCatalog()
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Activo");

        var empty = await Assert.ThrowsAsync<PurchasesDomainException>(() => Build(supplier, "usd", []));

        Assert.Equal(PurchasesErrorCodes.Empty, empty.Code);
    }

    [Fact]
    public async Task AVariantOutsideTheCatalogIsLineNotInCatalog()
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Activo");
        var known = _catalog.Add("SKU-1", "Camiseta", 19m);

        var missing = await Assert.ThrowsAsync<PurchasesDomainException>(() => Build(
            supplier,
            "COP",
            [new PurchaseOrderLineInput(known.VariantId, 1m, 1m), new PurchaseOrderLineInput(Guid.CreateVersion7(), 1m, 1m)]));

        Assert.Equal(PurchasesErrorCodes.LineNotInCatalog, missing.Code);
    }

    [Fact]
    public async Task LinesComeBackInBodyOrderWithTheCurrentCatalogData()
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Activo");
        var first = _catalog.Add("SKU-B", "Pantalón – 32", 5m);
        var second = _catalog.Add("SKU-A", "Camiseta – M / Azul", 19m);

        var drafts = await Build(
            supplier,
            "COP",
            [new PurchaseOrderLineInput(second.VariantId, 2m, 10m), new PurchaseOrderLineInput(first.VariantId, 1.5m, 20.25m)]);

        Assert.Equal(
            [
                new PurchaseOrderLineDraft(second.VariantId, "SKU-A", "Camiseta – M / Azul", 2m, 10m, 19m),
                new PurchaseOrderLineDraft(first.VariantId, "SKU-B", "Pantalón – 32", 1.5m, 20.25m, 5m),
            ],
            drafts);
    }

    // Mayorista fase 3: con proveedor vinculado la moneda del cuerpo no importa (D-11), así que su chequeo no corre.
    [Fact]
    public async Task ALinkedSupplierSkipsTheCurrencyCheck()
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Mayorista", linkedTenantId: Guid.CreateVersion7());
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);

        var build = await new PurchaseOrderLineBuilder(_suppliers, _catalog, _wholesale).BuildAsync(
            TenantId,
            new PreviewPurchaseOrderCommand(TenantId, supplier, "XYZ", null, [new PurchaseOrderLineInput(item.VariantId, 1m, null)]),
            CancellationToken.None);

        Assert.Equal("USD", build.Currency);
    }

    // Spec 2026-11-19: el flag vivo viaja en el armado y el borrador sigue trayendo el costo (la vista previa lo usa); quien lo
    // descarta es el dominio. Una orden externa nunca se oculta, aunque la lista de algún mayorista lo esté.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALinkedBuildCarriesTheLiveShowPricesAndStillTheCost(bool showPrices)
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Mayorista", linkedTenantId: Guid.CreateVersion7());
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        _wholesale.ShowPrices = showPrices;

        var build = await new PurchaseOrderLineBuilder(_suppliers, _catalog, _wholesale).BuildAsync(
            TenantId,
            new PreviewPurchaseOrderCommand(TenantId, supplier, "COP", null, [new PurchaseOrderLineInput(item.VariantId, 1m, null)]),
            CancellationToken.None);

        Assert.Equal(showPrices, build.ShowPrices);
        Assert.Equal(10000m, Assert.Single(build.Lines).UnitCost);
    }

    [Fact]
    public async Task AnExternalBuildAlwaysShowsPrices()
    {
        var supplier = Guid.CreateVersion7();
        _suppliers.Add(supplier, "Activo");
        var variant = _catalog.Add("SKU-1", "Camiseta", 19m);
        _wholesale.ShowPrices = false;

        var build = await new PurchaseOrderLineBuilder(_suppliers, _catalog, _wholesale).BuildAsync(
            TenantId,
            new PreviewPurchaseOrderCommand(TenantId, supplier, "COP", null, [new PurchaseOrderLineInput(variant.VariantId, 1m, 5m)]),
            CancellationToken.None);

        Assert.True(build.ShowPrices);
    }

    private readonly FakeWholesaleCatalog _wholesale = new();

    private async Task<IReadOnlyList<PurchaseOrderLineDraft>> Build(
        Guid supplierId, string currency, IReadOnlyList<PurchaseOrderLineInput> lines) =>
        (await new PurchaseOrderLineBuilder(_suppliers, _catalog, _wholesale).BuildAsync(
            TenantId, new PreviewPurchaseOrderCommand(TenantId, supplierId, currency, null, lines), CancellationToken.None)).Lines;
}
