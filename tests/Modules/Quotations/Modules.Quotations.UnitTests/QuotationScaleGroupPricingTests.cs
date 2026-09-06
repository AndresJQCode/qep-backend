using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleGroupPricingTests
{
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid ProductB = Guid.NewGuid();
    private static readonly Guid ProductC = Guid.NewGuid();

    private static QuotationPriceScaleRef Scale(
        bool allowGrouping, int multiple = 3, decimal discount = 5m, int fromUnit = 5, int toUnit = 48) =>
        new(fromUnit, toUnit, discount, QuotationPriceScaleRestriction.Multiple, multiple, null,
            allowGrouping);

    private static QuotationPriceScaleRef Packages(int packagingUnit = 12) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    private static Dictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> Catalog(
        params (Guid ProductId, QuotationPriceScaleRef Scale)[] entries) =>
        entries.ToDictionary(
            entry => entry.ProductId,
            entry => (IReadOnlyCollection<QuotationPriceScaleRef>)[entry.Scale]);

    private static QuotationLinePricing For(IReadOnlyList<QuotationLinePricing> result, Guid itemId) =>
        result.Single(line => line.ItemId == itemId);

    // El caso del requisito: 10 + 8 + 12 = 30, multiplo de 3, y las tres reciben su descuento.
    //
    // Pero no por la misma via, y por eso se afirma linea por linea: 10 y 8 no cumplen solas y
    // las rescata el total; 12 si cumple sola, asi que conserva su escala por su cuenta y ni
    // siquiera queda marcada como agrupada. Su cantidad sigue sumando al total que rescata a las
    // otras dos.
    [Fact]
    public void GroupedLinesSatisfyTheMultipleTogether()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m),
                new QuotationPricingLine(c, ProductC, 12m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: true)),
                (ProductC, Scale(allowGrouping: true))));

        Assert.All(result, line => Assert.Equal(5m, line.DiscountPercentage));

        Assert.True(For(result, a).Grouped);
        Assert.Equal(30m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.True(For(result, b).Grouped);
        Assert.Equal(30m, For(result, b).Restriction!.EvaluatedQuantity);

        Assert.False(For(result, c).Grouped);
        Assert.Equal(12m, For(result, c).Restriction!.EvaluatedQuantity);
    }

    // 10 + 13 = 23: le falta 1 unidad para 24. Ninguna de las dos cumple sola, el total tampoco,
    // y las dos reportan el mismo total y el mismo faltante.
    [Fact]
    public void GroupedLinesThatMissTheMultipleLoseTheScale()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 13m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: true))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.True(line.Grouped));
        Assert.All(result, line => Assert.Equal(23m, line.Restriction!.EvaluatedQuantity));
        Assert.All(result, line => Assert.Equal(1m, line.Restriction!.Shortfall));
        Assert.All(
            result,
            line => Assert.Equal("quotation.item.quantity_not_multiple", line.Restriction!.Code));
    }

    // Sin el switch, cada linea valida su multiplo sola: 10 % 3 y 8 % 3 fallan las dos.
    [Fact]
    public void UngroupedLinesValidateOnTheirOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: false)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.False(line.Grouped));
        Assert.Equal(10m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(8m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // El flag es condicion de pertenencia: dos escalas identicas en Desde/Hasta/Multiplo no
    // agrupan si solo una lo tiene. La que lo tiene queda sola con su propia cantidad.
    [Fact]
    public void ALineWithoutTheFlagNeverJoinsTheGroup()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 9m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.Equal(9m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(8m, For(result, b).Restriction!.EvaluatedQuantity);
        Assert.Equal(0m, For(result, b).DiscountPercentage);
    }

    // Escalas con distinto paso son grupos distintos: nunca hay ambiguedad sobre contra que
    // numero se compara el total.
    [Fact]
    public void ScalesWithADifferentStepFormSeparateGroups()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 9m),
                new QuotationPricingLine(b, ProductB, 10m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, multiple: 3)),
                (ProductB, Scale(allowGrouping: true, multiple: 4))));

        Assert.Equal(9m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(10m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // El descuento queda fuera de la clave del grupo: agrupan igual, y cada linea conserva el
    // de su propia escala.
    [Fact]
    public void GroupingIgnoresTheDiscountAndEachLineKeepsItsOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, discount: 10m)),
                (ProductB, Scale(allowGrouping: true, discount: 15m))));

        Assert.Equal(10m, For(result, a).DiscountPercentage);
        Assert.Equal(15m, For(result, b).DiscountPercentage);
    }

    // La unidad de empaque nunca agrupa y nunca lanza desde aca: 6 no es empaque entero de 12,
    // asi que la linea pierde la escala sin tumbar la operacion.
    [Fact]
    public void PackagingUnitIsEvaluatedPerLineAndNeverThrows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 6m),
                new QuotationPricingLine(b, ProductB, 6m)
            ],
            Catalog((ProductA, Packages()), (ProductB, Packages())));

        Assert.All(result, line => Assert.False(line.Grouped));
        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.Equal(6m, line.Restriction!.EvaluatedQuantity));
    }

    // Una cantidad que no cae en ninguna escala sigue sin descuento y sin restriccion que
    // reportar: no hay nada que la pantalla deba explicar.
    [Fact]
    public void ALineOutsideEveryScaleHasNoRestriction()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 2m)],
            Catalog((ProductA, Scale(allowGrouping: true))));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Null(For(result, a).Restriction);
        Assert.Null(For(result, a).Scale);
    }

    // Un producto que ya no existe en el catalogo no tumba el recalculo de las demas lineas.
    [Fact]
    public void AMissingProductLeavesItsLineWithoutDiscount()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 9m)],
            Catalog((ProductB, Scale(allowGrouping: true))));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Null(For(result, a).Restriction);
    }

    // Una linea que cumple el multiplo sola conserva su descuento aunque el total del grupo
    // falle: la agrupacion existe para rescatar a las que no cumplen, no para hundir a las que
    // si. A=6 cumple (6 % 3), B=10 no; el total 16 tampoco, pero eso es cosa de B.
    //
    // No cambia el veredicto de B: con multiplo puro toda linea que cumple es congruente con 0
    // modulo el paso, asi que sacarla de la suma deja el mismo resto. 16 % 3 y 10 % 3 dan 1.
    [Fact]
    public void ALineThatSatisfiesTheMultipleAloneKeepsItsScaleWhenTheGroupFails()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 6m),
                new QuotationPricingLine(b, ProductB, 10m)
            ],
            Catalog((ProductA, Scale(allowGrouping: true)), (ProductB, Scale(allowGrouping: true))));

        var lineA = For(result, a);
        Assert.Equal(5m, lineA.DiscountPercentage);
        Assert.False(lineA.Grouped);
        Assert.Equal(6m, lineA.Restriction!.EvaluatedQuantity);

        var lineB = For(result, b);
        Assert.Equal(0m, lineB.DiscountPercentage);
        Assert.True(lineB.Grouped);
        Assert.Equal("quotation.item.quantity_not_multiple", lineB.Restriction!.Code);
        Assert.Equal(16m, lineB.Restriction.EvaluatedQuantity);
    }
}
