using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationDiscountResolverTests
{
    // Ejemplo del propio documento: 1-9 sin descuento, 10-19 5%, 20+ 10%. Multiplo de 1 desde la
    // primera unidad: la restriccion existe (es obligatoria en Catalog) pero no descarta ninguna
    // cantidad, que es lo que estas pruebas quieren aislar.
    private static readonly QuotationPriceScaleRef[] Scales =
    [
        new(1, 9, 0m, QuotationPriceScaleRestriction.Multiple, 1, null),
        new(10, 19, 5m, QuotationPriceScaleRestriction.Multiple, 1, null),
        new(20, int.MaxValue, 10m, QuotationPriceScaleRestriction.Multiple, 1, null)
    ];

    [Theory]
    [InlineData(1, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 5)]
    [InlineData(19, 5)]
    [InlineData(20, 10)]
    [InlineData(1000, 10)]
    public void ResolveReturnsTheMatchingScaleDiscount(decimal quantity, decimal expected)
    {
        Assert.Equal(expected, QuotationDiscountResolver.Resolve(Scales, quantity)?.Discount ?? 0m);
    }

    // Decision confirmada: cantidad fuera de cualquier escala definida -> 0%.
    [Fact]
    public void ResolveReturnsZeroWhenNoScaleCoversTheQuantity()
    {
        QuotationPriceScaleRef[] gapScales =
            [new(10, 19, 5m, QuotationPriceScaleRestriction.Multiple, 1, null)];

        Assert.Null(QuotationDiscountResolver.Resolve(gapScales, 3m));
    }

    [Fact]
    public void ResolveReturnsZeroWhenThereAreNoScalesAtAll()
    {
        Assert.Null(QuotationDiscountResolver.Resolve([], 15m));
    }
}
