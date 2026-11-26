using BuildingBlocks.Application;
using FluentValidation;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>D-10 y D-11: «Así lo ve el distribuidor» mientras se edita la lista, sin guardarla.</summary>
public sealed class PreviewPriceListTests
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly FakeCatalogReader _catalog = new();
    private readonly FakeWholesalePriceLists _priceLists = new();
    private FakeExecutionContext _context;

    public PreviewPriceListTests()
    {
        _context = new FakeExecutionContext(_tenant, WholesalePermissions.PriceListManage);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PreviewPriceListHandler Handler(string currency = "COP") =>
        new(_context, new FakeTenantDefaultCurrency(currency), _priceLists, _catalog, new PreviewPriceListValidator());

    [Fact]
    public async Task PreviewUsesTheWholesalerCurrencyAndRounding()
    {
        _catalog.AddProduct("A-1", "Olla", categoryId: null, cop: 10000m, usd: 3m);
        _catalog.AddProduct("A-2", "Sartén", categoryId: null, cop: 10001m, usd: 4m);

        var preview = await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -15m, "AllActive", []), Ct);

        Assert.Equal("COP", preview.Currency);
        // 10000 × 0,85 = 8500; 10001 × 0,85 = 8500,85 → 8501 con COP sin decimales al más cercano.
        Assert.Equal([("Olla", 10000m, 8500m), ("Sartén", 10001m, 8501m)],
            preview.Items.Select(item => (item.Name, item.BasePrice, item.ListPrice)));
        Assert.Equal([("COP", 20)], _catalog.SampleCalls);
    }

    // Ruling W8: con datos concretos. La variante de una categoría que la lista no ve no sale; la de una que sí, sale con
    // su precio calculado; la que no tiene precio en la moneda del mayorista tampoco sale (el filtro real lo prueba la
    // integración, WholesaleAdaptersTests.SampleSkipsVariantsWithoutPriceAndInactiveProducts).
    [Fact]
    public async Task PreviewSkipsVariantsWithoutPriceInTheWholesalerCurrency()
    {
        var visible = _catalog.AddCategory("Hogar");
        var hidden = _catalog.AddCategory("Ferretería");
        _catalog.AddProduct("H-1", "Olla", visible, cop: 20000m);
        _catalog.AddProduct("H-2", "Tetera", visible, cop: null, usd: 9m);
        _catalog.AddProduct("F-1", "Martillo", hidden, cop: 30000m);

        var preview = await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -10m, "Categories", [visible]), Ct);

        var item = Assert.Single(preview.Items);
        Assert.Equal(("H-1", "Olla", 20000m, 18000m), (item.Code, item.Name, item.BasePrice, item.ListPrice));
        Assert.Equal(1, preview.TotalProducts);
    }

    [Fact]
    public async Task PreviewPassesTheReaderTotalAndItemsThrough()
    {
        for (var index = 0; index < 25; index++)
        {
            _catalog.AddProduct($"P-{index:D2}", $"Producto {index:D2}", categoryId: null, cop: 1000m);
        }

        var preview = await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, 0m, "AllActive", []), Ct);

        Assert.Equal(25, preview.TotalProducts);
        Assert.Equal(20, preview.Items.Count);
        Assert.Equal("Producto 00", preview.Items[0].Name);
        Assert.All(preview.Items, item => Assert.Equal(item.BasePrice, item.ListPrice));
    }

    [Fact]
    public async Task PreviewPassesNullCategoriesForAllActive()
    {
        var category = _catalog.AddCategory("Hogar");

        await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -10m, "AllActive", [category]), Ct);

        Assert.Null(Assert.Single(_catalog.SampleCategoryIds));
        Assert.Equal(0, _catalog.FindCategoriesCalls);
    }

    [Fact]
    public async Task PreviewWithOnlyDeletedCategoriesIsEmptyWithoutAskingTheCatalogForProducts()
    {
        var preview = await Handler().HandleAsync(
            new PreviewPriceListCommand(_tenant, -10m, "Categories", [Guid.CreateVersion7()]), Ct);

        Assert.Equal(("COP", 0), (preview.Currency, preview.TotalProducts));
        Assert.Empty(preview.Items);
        Assert.Empty(_catalog.SampleCalls);
    }

    [Fact]
    public async Task PreviewValidatesTheTerms()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(new PreviewPriceListCommand(_tenant, 1000m, "AllActive", []), Ct));

        Assert.Equal(["AdjustmentPercent"], error.Errors.Select(failure => failure.PropertyName).Distinct());
        Assert.Empty(_catalog.SampleCalls);
    }

    [Fact]
    public async Task PreviewWritesNothing()
    {
        // El handler no recibe repositorio, unidad de trabajo ni auditoría: no tiene con qué escribir. Se fija la forma del
        // constructor para que sumarle uno sea una decisión visible.
        var parameters = typeof(PreviewPriceListHandler).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IWholesaleUnitOfWork), parameters);
        Assert.DoesNotContain(typeof(IWholesaleAuditPublisher), parameters);
        // Spec 2026-11-22: lee el precio por IWholesalePriceLists, que no escribe (no tiene Add ni Remove).
        Assert.DoesNotContain(parameters, type => type.GetMethods().Any(method => method.Name is "Add" or "Remove"));
        _catalog.AddProduct("A-1", "Olla", categoryId: null, cop: 10000m);
        var preview = await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -10m, "AllActive", []), Ct);
        Assert.Single(preview.Items);
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenBeforeReadingTheCatalog()
    {
        _context = new FakeExecutionContext(Guid.CreateVersion7(), WholesalePermissions.PriceListManage);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -10m, "AllActive", []), Ct));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(_catalog.SampleCalls);
    }

    // Spec 2026-11-20: exclusions and hiddenProducts.
    [Fact]
    public async Task PreviewPassesTheExclusionsToTheReaderAndCountsWhatTheyHide()
    {
        var home = _catalog.AddCategory("Hogar");
        var kitchen = _catalog.AddCategory("Cocina", parentId: home);
        _catalog.AddProduct("H-1", "Olla", home, cop: 20000m);
        _catalog.AddProduct("K-1", "Cuchillo", kitchen, cop: 30000m);
        var loose = _catalog.AddProduct("L-1", "Tetera", home, cop: 40000m);

        var preview = await Handler().HandleAsync(
            new PreviewPriceListCommand(
                _tenant, -10m, "AllActive", [],
                [new PriceListExclusionInput("Category", kitchen), new PriceListExclusionInput("Product", _catalog.ProductIdOf(loose))]),
            Ct);

        Assert.Equal(["Olla"], preview.Items.Select(item => item.Name));
        Assert.Equal((1, 2), (preview.TotalProducts, preview.HiddenProducts));
        Assert.Contains(_catalog.SampleExclusions, call => call.Categories!.SequenceEqual([kitchen]) && call.Products!.Count == 1);
    }

    [Fact]
    public async Task PreviewWithoutExclusionsHidesNothingAndAsksTheReaderOnce()
    {
        _catalog.AddProduct("H-1", "Olla", categoryId: null, cop: 20000m);

        var preview = await Handler().HandleAsync(new PreviewPriceListCommand(_tenant, -10m, "AllActive", []), Ct);

        Assert.Equal(0, preview.HiddenProducts);
        Assert.Single(_catalog.SampleCalls);
    }
}
