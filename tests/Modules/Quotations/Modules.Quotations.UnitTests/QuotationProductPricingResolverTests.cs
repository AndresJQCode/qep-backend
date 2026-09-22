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
            UnitPriceCop: 100_000m,
            UnitPriceUsd: 25m,
            Scales:
            [
                new QuotationPriceScaleRef(
                    1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, null),
                new QuotationPriceScaleRef(10, 99, 10m, null, null, null)
            ],
            TaxPercentage: null);

    [Fact]
    public async Task RejectsPricingAProductWithAnIncompleteScale()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductWithAnIncompleteScale(productId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            QuotationProductPricingResolver.ResolveAsync(
                lookup, TenantId, productId, 3m, QuotationCurrency.Cop,
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
                lookup, TenantId, [(productId, 3m)], QuotationCurrency.Usd,
                TestContext.Current.CancellationToken));

        Assert.Equal(IncompleteCode, error.Code);
    }

    [Fact]
    public async Task PricesAProductWhoseScalesAreComplete()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(new QuotationProductPricingRef(
            productId, TenantId, "Vela de soja", true, 100_000m, null,
            [new QuotationPriceScaleRef(1, 9, 5m, QuotationPriceScaleRestriction.Multiple, 1, null)],
            null));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 3m, QuotationCurrency.Cop,
            TestContext.Current.CancellationToken);

        Assert.Equal(5m, priced.Pricing.DiscountPercentage);
    }

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
            UnitPriceCop: 100_000m,
            UnitPriceUsd: 25m,
            Scales:
            [
                new QuotationPriceScaleRef(
                    1, 999, 15m, QuotationPriceScaleRestriction.PackagingUnit, null, 12)
            ],
            TaxPercentage: null);

    [Fact]
    public async Task AQuantityThatIsNotAWholePackageIsPricedWithoutDiscount()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubPricingLookup(ProductSoldInPackagesOfTwelve(productId));

        var priced = await QuotationProductPricingResolver.ResolveAsync(
            lookup, TenantId, productId, 13m, QuotationCurrency.Cop,
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
            lookup, TenantId, productId, 24m, QuotationCurrency.Cop,
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
            lookup, TenantId, [(productId, 13m)], QuotationCurrency.Usd,
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, priced[productId].DiscountPercentage);
    }
}
