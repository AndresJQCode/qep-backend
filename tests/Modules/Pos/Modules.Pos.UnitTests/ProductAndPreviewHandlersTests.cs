using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class ProductAndPreviewHandlersTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];
    private static readonly string[] Discounter = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];

    private static SearchPosProductsHandler Search(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new SearchPosProductsValidator());

    private static FindPosProductByCodeHandler ByCode(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new FindPosProductByCodeValidator());

    private static PreviewPosSaleHandler Preview(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new PreviewPosSaleValidator());

    private static PosPreviewLineRequest[] WorkedExampleCart() =>
    [
        new(Shampoo, 2m, 10m),
        new(Avena, 1.5m, 0m),
        new(Jabon, 3m, 0m),
    ];

    [Fact]
    public async Task SearchMarksAProductWithoutAPesoPriceAsUnsellable()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19);
        bed.Products.Add("CR-77", "Crema", null, 19);

        var page = await Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Total);
        var shampoo = page.Items.Single(item => item.Code == "SH-400");
        var cream = page.Items.Single(item => item.Code == "CR-77");
        Assert.True(shampoo.Sellable);
        Assert.Null(shampoo.UnsellableReason);
        Assert.Equal(11_900m, shampoo.UnitPrice);
        Assert.False(cream.Sellable);
        Assert.Equal("PriceMissing", cream.UnsellableReason);
        Assert.Null(cream.UnitPrice);
    }

    [Fact]
    public async Task ByCodeIsExactAndReturnsAnInactiveProductMarked()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19);
        bed.Products.Add("JB-09", "Jabón viejo", 2_000m, 5, active: false);

        var found = await ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "SH-400"), TestContext.Current.CancellationToken);
        var inactive = await ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "JB-09"), TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "sh-400"), TestContext.Current.CancellationToken));

        Assert.True(found.Sellable);
        Assert.False(inactive.Sellable);
        Assert.Equal("Inactive", inactive.UnsellableReason);
        Assert.Equal("pos.product.not_found", missing.Code);
    }

    [Fact]
    public async Task ProductReadsNeedTheSalePermission()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => Search(bed, PosPermissions.SaleRead).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => ByCode(bed, PosPermissions.SaleRead).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "SH-400"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAndByCodeValidateTheirInputs()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<ValidationException>(() => Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 61), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, " "), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, new string('X', 61)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreviewComputesTheWorkedExampleOnTheServer()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        var preview = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, WorkedExampleCart()), TestContext.Current.CancellationToken);

        Assert.Equal(34_042.86m, preview.Subtotal);
        Assert.Equal(3_847.14m, preview.TaxAmount);
        Assert.Equal(2_380m, preview.DiscountAmount);
        Assert.Equal(37_890m, preview.Total);
        Assert.False(preview.ZeroTotalNotAllowed);
        var shampoo = preview.Lines[0];
        Assert.Equal("SH-400", shampoo.Code);
        Assert.Equal(11_900m, shampoo.UnitPrice);
        Assert.Equal(19, shampoo.TaxPercentage);
        Assert.Equal((2_380m, 3_420m, 18_000m, 21_420m), (shampoo.DiscountAmount, shampoo.TaxAmount, shampoo.Subtotal, shampoo.LineTotal));
    }

    // Un producto borrado vuelve NotFound y fuera de los totales, no como 422 (spec, decisión 34).
    [Fact]
    public async Task PreviewKeepsUnsellableLinesOutOfTheTotals()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: Shampoo);
        bed.Products.Add("AV-01", "Avena granel (kg)", 5_000m, 0, active: false, id: Avena);
        bed.Products.Add("JB-03", "Jabón", null, 5, id: Jabon);
        var gone = Guid.CreateVersion7();

        var preview = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m), new(Avena, 1m, 0m), new(Jabon, 1m, 0m), new(gone, 1m, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(11_900m, preview.Total);
        Assert.Equal(
            new[] { (true, (string?)null), (false, "Inactive"), (false, "PriceMissing"), (false, "NotFound") },
            preview.Lines.Select(line => (line.Sellable, line.UnsellableReason)).ToArray());
        var notFound = preview.Lines[3];
        Assert.Null(notFound.Code);
        Assert.Null(notFound.Name);
        Assert.Equal(0m, notFound.LineTotal);
    }

    // Review Focus 2.
    [Fact]
    public async Task PreviewWithTheSameProductTwiceReturnsTwoLines()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: Shampoo);

        var preview = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 10m), new(Shampoo, 1m, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(10_710m + 11_900m, preview.Total);
    }

    [Fact]
    public async Task PreviewWithADiscountWithoutThePermissionIs403()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, WorkedExampleCart()), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.discount_not_allowed", error.Code);
    }

    // Un carrito con productos a precio 0 se tiene que poder dibujar; lo que no se puede es cobrarlo.
    [Fact]
    public async Task PreviewFlagsAZeroTotalWithoutTheDiscountPermission()
    {
        var bed = new PosTestBed();
        bed.Products.Add("MU-00", "Muestra gratis", 0m, 0, id: Shampoo);

        var seller = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m)]), TestContext.Current.CancellationToken);
        var discounter = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m)]), TestContext.Current.CancellationToken);

        Assert.True(seller.ZeroTotalNotAllowed);
        Assert.False(discounter.ZeroTotalNotAllowed);
    }

    // Spec literal (preflight F-13): total 0 sin pos.sale.discount se marca aunque ninguna línea sea
    // vendible; "Cobrar" ya está bloqueado por su propio motivo, pero el flag no depende de eso.
    [Fact]
    public async Task PreviewFlagsZeroWhenNoLineIsSellableAndTheCallerCannotDiscount()
    {
        var bed = new PosTestBed();

        var seller = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Guid.CreateVersion7(), 1m, 0m)]), TestContext.Current.CancellationToken);
        var discounter = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Guid.CreateVersion7(), 1m, 0m)]), TestContext.Current.CancellationToken);

        Assert.True(seller.ZeroTotalNotAllowed);
        Assert.False(discounter.ZeroTotalNotAllowed);
    }

    [Theory]
    [InlineData("1.005", "0")]
    [InlineData("0", "0")]
    [InlineData("100000", "0")]
    [InlineData("1", "100.5")]
    [InlineData("1", "7.005")]
    public async Task PreviewRejectsQuantityOrDiscountOutOfRangeOrScale(string quantity, string discount)
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        await Assert.ThrowsAsync<ValidationException>(() => Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo,
                decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture))]),
            TestContext.Current.CancellationToken));
    }
}
