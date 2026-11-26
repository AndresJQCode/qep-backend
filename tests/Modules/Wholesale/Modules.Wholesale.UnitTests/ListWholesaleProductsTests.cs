using BuildingBlocks.Application;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-20: el selector de productos a ocultar de una lista.</summary>
public sealed class ListWholesaleProductsTests
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly FakeCatalogReader _catalog = new();
    private readonly FakeExecutionContext _context;

    public ListWholesaleProductsTests()
    {
        _context = new FakeExecutionContext(_tenant, WholesalePermissions.PriceListManage);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ListWholesaleProductsHandler Handler(string currency = "COP", FakeExecutionContext? context = null) =>
        new(context ?? _context, new FakeTenantDefaultCurrency(currency), _catalog);

    [Fact]
    public async Task ItListsActivePricedProductsByNameWithCategoryNameAndTotal()
    {
        var home = _catalog.AddCategory("Hogar");
        _catalog.AddProduct("B-1", "Sartén", home, cop: 10m, imageUrl: "https://cdn.example.co/s.png");
        _catalog.AddProduct("A-1", "Olla", home, cop: 10m);
        _catalog.AddProduct("U-1", "Dólares", home, cop: null, usd: 3m);
        _catalog.AddProduct("I-1", "Inactivo", home, cop: 10m, isActive: false);

        var page = await Handler().HandleAsync(new ListWholesaleProductsQuery(_tenant, null, null, 1, 50), Ct);

        Assert.Equal(["Olla", "Sartén"], page.Items.Select(item => item.Name));
        Assert.Equal((2, 1, 50), (page.Total, page.Page, page.PageSize));
        var pan = page.Items[1];
        Assert.Equal(("B-1", home, "Hogar", 1, "https://cdn.example.co/s.png"), (pan.Code, pan.CategoryId, pan.CategoryName, pan.VariantCount, pan.ImageUrl));
    }

    [Fact]
    public async Task SearchAndCategoryFilterReachTheReaderAndThePageSizeIsCappedAtFifty()
    {
        var home = _catalog.AddCategory("Hogar");
        var kitchen = _catalog.AddCategory("Cocina", home);
        _catalog.AddProduct("A-1", "Olla", kitchen, cop: 10m);
        _catalog.AddProduct("A-2", "Pala", categoryId: null, cop: 10m);

        var page = await Handler().HandleAsync(new ListWholesaleProductsQuery(_tenant, "  olla ", home, 0, 500), Ct);

        Assert.Equal(["Olla"], page.Items.Select(item => item.Name));
        Assert.Equal((1, 50), (page.Page, page.PageSize));
        Assert.Equal([(home, "olla", 1, 50)], _catalog.ProductSearchCalls.Select(call => (call.CategoryId, call.Search, call.Page, call.PageSize)));
    }

    [Fact]
    public async Task WithoutThePermissionItIs403()
    {
        var denied = new FakeExecutionContext(_tenant);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(context: denied).HandleAsync(new ListWholesaleProductsQuery(_tenant, null, null, 1, 50), Ct));
    }

    [Fact]
    public async Task AnAbsurdPageNumberIsClampedAndDoesNotOverflow()
    {
        _catalog.AddProduct("A-1", "Olla", categoryId: null, cop: 10m);

        var page = await Handler().HandleAsync(new ListWholesaleProductsQuery(_tenant, null, null, int.MaxValue, 50), Ct);

        Assert.Equal(ListWholesaleProductsHandler.MaxPage, page.Page);
        Assert.Empty(page.Items);
    }
}
