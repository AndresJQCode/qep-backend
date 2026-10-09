using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El bloqueo de un producto con escalas incompletas. La copia de escalas deja tramos sin
/// restricción, y un producto con uno solo de ésos no se cotiza hasta que alguien lo complete en
/// el catálogo: con qué múltiplo o empaque se vende es justo lo que falta, y ningún precio que
/// salga de acá sin eso es el correcto.
/// </summary>
public sealed class QuotationProductPricingResolverTests
{
    private const string IncompleteCode = "quotation.item.product_price_scales_incomplete";

    private static readonly Guid TenantId = Guid.NewGuid();

    // La escala incompleta no es la que cubre la cantidad a propósito: el bloqueo es del producto,
    // no del tramo en el que cae la línea.
    private static QuotationProductPricingRef ProductWithAnIncompleteScale(Guid productId) =>
        new(
            productId,
            TenantId,
            "Vela de soja",
            IsActive: true,
            Prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["USD"] = 25m },
            Scales:
            [
                new QuotationPriceScaleRef(
                    1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, []),
                new QuotationPriceScaleRef(10, 99, 10m, null, null, [])
            ],
            TaxPercentage: null);

    [Fact]
    public async Task RejectsPricingAProductWithAnIncompleteScale()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductWithAnIncompleteScale(productId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            QuotationProductPricingResolver.ResolveAsync(
                lookup, TenantId, productId, 3m, "COP", isRetail: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(IncompleteCode, error.Code);
    }

    // El cambio de moneda revaloriza todas las líneas: también es fijar un precio.
    [Fact]
    public async Task RejectsRepricingLinesOfAProductWithAnIncompleteScale()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductWithAnIncompleteScale(productId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            QuotationProductPricingResolver.ResolveManyAsync(
                lookup, TenantId, [(productId, 3m)], "USD", isRetail: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(IncompleteCode, error.Code);
    }

    [Fact]
    public async Task PricesAProductWhoseScalesAreComplete()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(new QuotationProductPricingRef(
            productId, TenantId, "Vela de soja", true, new Dictionary<string, decimal> { ["COP"] = 100_000m },
            [new QuotationPriceScaleRef(1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, [])],
            null));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 3m, "COP", isRetail: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(5m, priced.Pricing.DiscountPercentage);
    }

    // No conversion, ever: an EUR quotation needs the product's EUR price, and a product without one
    // is refused — never priced from COP or USD.
    [Fact]
    public async Task AnEurQuotationRejectsAProductWithoutAnEurPrice()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(PricedProduct(productId, new() { ["COP"] = 100_000m, ["USD"] = 25m }));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            QuotationProductPricingResolver.ResolveAsync(
                lookup, TenantId, productId, 3m, "EUR", isRetail: false, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.item.product_price_unavailable", error.Code);
    }

    [Fact]
    public async Task AnEurQuotationPricesWithTheEurPrice()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(PricedProduct(productId, new() { ["COP"] = 100_000m, ["EUR"] = 23m }));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 3m, "EUR", isRetail: false, TestContext.Current.CancellationToken);

        Assert.Equal(23m, priced.Pricing.UnitPrice);
    }

    private static QuotationProductPricingRef PricedProduct(Guid productId, Dictionary<string, decimal> prices) =>
        new(productId, TenantId, "Vela de soja", IsActive: true, prices,
            [new QuotationPriceScaleRef(1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, [])],
            TaxPercentage: null);

    private sealed class StubPricingLookup(QuotationProductPricingRef product)
        : IQuotationProductPricingLookup
    {
        public Task<QuotationProductPricingRef?> FindAsync(
            Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationProductPricingRef?>(
                productId == product.Id ? product : null);

        public Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductPricingRef>>(
                productIds.Contains(product.Id)
                    ? new Dictionary<Guid, QuotationProductPricingRef> { [product.Id] = product }
                    : new Dictionary<Guid, QuotationProductPricingRef>());
    }

    // ---- Ninguna restriccion de escala bloquea la linea (2026-09-22) ----
    //
    // Decision del developer: una cantidad que no cumple la regla de su escala NO impide guardar
    // la linea; lo unico que pasa es que esa escala no aplica y la linea va sin descuento. Hasta
    // hoy `Multiple` ya se comportaba asi y `PackagingUnit` conservaba un 422, que se quito.
    private static QuotationProductPricingRef ProductSoldInPackagesOfTwelve(Guid productId) =>
        new(
            productId,
            TenantId,
            "Vela de soja",
            IsActive: true,
            Prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["USD"] = 25m },
            Scales:
            [
                new QuotationPriceScaleRef(
                    1, 999, 15m, QuotationPriceScaleRestriction.PackagingUnit, null, [12])
            ],
            TaxPercentage: null);

    [Fact]
    public async Task AQuantityThatIsNotAWholePackageIsPricedWithoutDiscount()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductSoldInPackagesOfTwelve(productId));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 13m, "COP", isRetail: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, priced.Pricing.DiscountPercentage);
        Assert.Equal(100_000m, priced.Pricing.UnitPrice);
    }

    // Y la que si forma paquetes enteros conserva su descuento: quitar el bloqueo no es apagar
    // la restriccion, es cambiar que hace cuando no se cumple.
    [Fact]
    public async Task AWholeNumberOfPackagesStillEarnsTheDiscount()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductSoldInPackagesOfTwelve(productId));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 24m, "COP", isRetail: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(15m, priced.Pricing.DiscountPercentage);
    }

    // El cambio de moneda revaloriza todas las lineas y pasa por el mismo camino: tampoco lanza.
    [Fact]
    public async Task RepricingLinesThatMissTheirPackagingDoesNotThrow()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductSoldInPackagesOfTwelve(productId));

        var priced = await QuotationProductPricingResolver.ResolveManyAsync(
            lookup, TenantId, [(productId, 13m)], "USD", isRetail: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, priced[productId].DiscountPercentage);
    }
    // Cotizacion detal (decision del owner, 2026-09-23): "el sistema debe omitir cualquier escala
    // de precio y no validar restricciones". Un producto con escalas a medio configurar se cotiza
    // igual, porque en detal las escalas no se usan para nada.
    [Fact]
    public async Task RetailPricesAProductWithAnIncompleteScale()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductWithAnIncompleteScale(productId));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 3m, "COP", isRetail: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(100_000m, priced.Pricing.UnitPrice);
        Assert.Equal(0m, priced.Pricing.DiscountPercentage);
    }

    // Y omite la escala aunque este completa y la cantidad caiga en un tramo con descuento: en
    // detal ninguna linea descuenta, desde el momento en que se agrega.
    [Fact]
    public async Task RetailIgnoresAScaleThatWouldHaveGivenADiscount()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(new QuotationProductPricingRef(
            productId, TenantId, "Vela de soja", true, new Dictionary<string, decimal> { ["COP"] = 100_000m },
            [new QuotationPriceScaleRef(1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, [])],
            null));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 3m, "COP", isRetail: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, priced.Pricing.DiscountPercentage);
    }

    // El cambio de moneda de una cotizacion detal tampoco valida escalas.
    [Fact]
    public async Task RetailRepricesLinesOfAProductWithAnIncompleteScale()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductWithAnIncompleteScale(productId));

        var priced = await QuotationProductPricingResolver.ResolveManyAsync(
            lookup, TenantId, [(productId, 3m)], "USD", isRetail: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(25m, priced[productId].UnitPrice);
        Assert.Equal(0m, priced[productId].DiscountPercentage);
    }

}
