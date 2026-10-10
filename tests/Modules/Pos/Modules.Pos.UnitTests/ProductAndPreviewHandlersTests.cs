using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class ProductAndPreviewHandlersTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];
    private static readonly string[] Discounter = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];

    private static SearchPosProductsHandler Search(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Sessions, bed.Memberships, bed.DefaultCurrency, bed.Context(permissions),
            new SearchPosProductsValidator());

    private static FindPosProductByCodeHandler ByCode(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Sessions, bed.Memberships, bed.DefaultCurrency, bed.Context(permissions),
            new FindPosProductByCodeValidator());

    private static PreviewPosSaleHandler Preview(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Sessions, bed.Memberships, bed.DefaultCurrency, bed.Context(permissions),
            new PreviewPosSaleValidator());

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

    // La vista previa cotiza en la moneda con la que el POST va a cobrar: la de la caja abierta.
    [Fact]
    public async Task PreviewInAUsdSessionPricesWithTheUsdListAndMarksAProductWithoutIt()
    {
        var bed = new PosTestBed();
        bed.OpenSessionInStore(currency: "USD");
        bed.Products.Add("SH-400", "Shampoo 400 ml", 40_000m, 0, id: Shampoo, priceUsd: 10m);
        bed.Products.Add("JB-03", "Jabón", 2_990m, 0, id: Jabon);

        var preview = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 2m, 0m), new(Jabon, 1m, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(20m, preview.Total);
        Assert.Equal(10m, preview.Lines[0].UnitPrice);
        Assert.Equal((false, "PriceMissing", (decimal?)null), (preview.Lines[1].Sellable, preview.Lines[1].UnsellableReason, preview.Lines[1].UnitPrice));
    }

    // Sin caja todavía, el catálogo se muestra en la moneda con la que se abriría: la del tenant.
    [Fact]
    public async Task SearchWithoutASessionPricesInTheTenantDefaultCurrency()
    {
        var bed = new PosTestBed { DefaultCurrency = new FakeTenantDefaultCurrency("USD") };
        bed.Products.Add("SH-400", "Shampoo 400 ml", 40_000m, 0, priceUsd: 10m);
        bed.Products.Add("CR-77", "Crema", 25_000m, 19);

        var page = await Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken);

        var shampoo = page.Items.Single(item => item.Code == "SH-400");
        var cream = page.Items.Single(item => item.Code == "CR-77");
        Assert.Equal(10m, shampoo.UnitPrice);
        Assert.True(shampoo.Sellable);
        Assert.Equal((false, "PriceMissing", (decimal?)null), (cream.Sellable, cream.UnsellableReason, cream.UnitPrice));
    }

    // La caja abierta manda sobre la moneda vigente del tenant: se congeló al abrir.
    [Fact]
    public async Task ByCodeWithAnOpenSessionPricesInTheSessionCurrencyNotTheTenantDefault()
    {
        var bed = new PosTestBed { DefaultCurrency = new FakeTenantDefaultCurrency("USD") };
        bed.OpenSessionInStore(currency: "COP");
        bed.Products.Add("SH-400", "Shampoo 400 ml", 40_000m, 0, priceUsd: 10m);

        var found = await ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "SH-400"), TestContext.Current.CancellationToken);

        Assert.Equal(40_000m, found.UnitPrice);
        Assert.True(found.Sellable);
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

    // La caja ve el mismo 422 que daría el POST antes de cobrar, con el mismo código.
    [Fact]
    public async Task PreviewRejectsATotalThatDoesNotFitLikeTheSale()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 20_000_000m, 19, id: Shampoo);

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 99_999m, 0m)]), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.total_too_large", error.Code);
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

    [Fact]
    public async Task PreviewRejectsANullLineAsValidationFailure()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        await Assert.ThrowsAsync<ValidationException>(() => Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [null!]), TestContext.Current.CancellationToken));
    }

    // Arrastre de B6: sin pos.sale.create, o con otro tenant en la ruta, la vista previa es 403.
    [Fact]
    public async Task PreviewWithoutTheCreatePermissionOrForAnotherTenantIs403()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();
        var cart = new PosPreviewLineRequest[] { new(Shampoo, 1m, 0m) };

        var noPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() => Preview(bed, PosPermissions.SaleRead).HandleAsync(
            new PreviewPosSaleCommand(TenantId, cart), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(Guid.CreateVersion7(), cart), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", noPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
    }

    [Fact]
    public async Task PreviewRejectsAnEmptyCartAndMoreThan200Lines()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        var empty = await Assert.ThrowsAsync<ValidationException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, []), TestContext.Current.CancellationToken));
        var tooMany = await Assert.ThrowsAsync<ValidationException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, Enumerable.Range(0, 201).Select(_ => new PosPreviewLineRequest(Shampoo, 1m, 0m)).ToArray()),
            TestContext.Current.CancellationToken));
        var atTheLimit = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, Enumerable.Range(0, 200).Select(_ => new PosPreviewLineRequest(Shampoo, 1m, 0m)).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Contains(empty.Errors, failure => failure.PropertyName == "Lines");
        Assert.Contains(tooMany.Errors, failure => failure.PropertyName == "Lines");
        Assert.Equal(200, atTheLimit.Lines.Count);
    }

    [Fact]
    public async Task SearchInAnEurTenantPricesInEurAndMarksProductsWithoutItAsUnsellable()
    {
        var bed = new PosTestBed { DefaultCurrency = new FakeTenantDefaultCurrency("EUR") };
        bed.Products.Add("SH-400", "Shampoo 400 ml", null, 0,
            prices: new Dictionary<string, decimal> { ["COP"] = 40_000m, ["EUR"] = 9.5m });
        bed.Products.Add("CR-77", "Crema", 25_000m, 19);

        var page = await Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken);

        Assert.Equal(9.5m, page.Items.Single(item => item.Code == "SH-400").UnitPrice);
        var cream = page.Items.Single(item => item.Code == "CR-77");
        Assert.Equal((false, "PriceMissing", (decimal?)null), (cream.Sellable, cream.UnsellableReason, cream.UnitPrice));
    }
}
