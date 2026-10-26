using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Catalog → Pos, las categorías de la caja (spec 2026-10-26 §3.4). El repositorio cuenta productos
/// activos **directos**; el adaptador los acumula hacia las raíces, para que el chip de una categoría
/// diga cuántos encuentra el filtro (que incluye las descendientes). El total sale de una consulta
/// propia: la suma de las raíces perdería los productos sin categoría.
/// </summary>
public sealed class PosCategoryLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActiveCountsRollUpFromLeavesToRoots()
    {
        var bebidas = Category("Bebidas");
        var gaseosas = Category("Gaseosas", bebidas);
        var cola = Category("Cola", gaseosas);
        var jugos = Category("Jugos", bebidas);
        var calzado = Category("Calzado");
        var lookup = new PosCategoryLookup(
            new FakeProductCategoryRepository((bebidas, 2), (gaseosas, 3), (cola, 1), (jugos, 1), (calzado, 0)),
            new FakeProductRepository());

        var list = await lookup.ListAsync(TenantId, Ct);

        Assert.Equal(
            [
                ("Bebidas", (Guid?)null, 7),
                ("Calzado", null, 0),
                ("Cola", gaseosas.Id.Value, 1),
                ("Gaseosas", bebidas.Id.Value, 4),
                ("Jugos", bebidas.Id.Value, 1),
            ],
            list.Items.Select(item => (item.Name, item.ParentId, item.ProductCount)));
        Assert.Equal(bebidas.Id.Value, list.Items[0].Id);
    }

    [Fact]
    public async Task TheTotalCountsEveryActiveProductCategorizedOrNot()
    {
        var bebidas = Category("Bebidas");
        var inactive = NewProduct("Vieja", bebidas.Id);
        inactive.Deactivate(Now);
        var products = new FakeProductRepository(
            NewProduct("Cola", bebidas.Id),
            NewProduct("Agua", bebidas.Id),
            NewProduct("Jabón", null),
            inactive,
            NewProduct("Ajeno", null, Guid.CreateVersion7()));
        var lookup = new PosCategoryLookup(new FakeProductCategoryRepository((bebidas, 2)), products);

        var list = await lookup.ListAsync(TenantId, Ct);

        Assert.Equal(2, list.Items.Where(item => item.ParentId is null).Sum(item => item.ProductCount));
        Assert.Equal(3, list.Total);
    }

    [Fact]
    public async Task AnotherTenantsCategoriesAreNotListed()
    {
        var own = Category("Bebidas");
        var foreign = Category("Ajena", tenantId: Guid.CreateVersion7());
        var lookup = new PosCategoryLookup(
            new FakeProductCategoryRepository((own, 1), (foreign, 5)), new FakeProductRepository());

        var list = await lookup.ListAsync(TenantId, Ct);

        var only = Assert.Single(list.Items);
        Assert.Equal((own.Id.Value, 1), (only.Id, only.ProductCount));
    }

    private static ProductCategory Category(string name, ProductCategory? parent = null, Guid? tenantId = null) =>
        ProductCategory.Create(ProductCategoryId.New(), tenantId ?? TenantId, name, parent?.Id, Now);

    private static Product NewProduct(string name, ProductCategoryId? categoryId, Guid? tenantId = null) =>
        Product.Create(
            ProductId.New(), tenantId ?? TenantId, name,
            ProductDetails.Empty with { CategoryId = categoryId },
            new ProductVariantInput
            {
                Code = $"P-{Guid.CreateVersion7():N}"[..12],
                Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1_000m } },
            },
            [],
            Now);
}
