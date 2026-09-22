using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleRestrictionRuleTests
{
    private static QuotationPriceScaleRef MultipleOf(int multiple, int fromUnit = 5) =>
        new(fromUnit, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, null);

    private static QuotationPriceScaleRef PackagesOf(int packagingUnit) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    // El multiplo se cuenta DESDE FromUnit: en una escala 5-48 de a 3 las cantidades validas son
    // 5, 8, 11 ... 47. Decision del developer el 2026-09-21, que revierte el criterio crudo del
    // 2026-09-06 y vuelve al que traia el CRM.
    //
    // El piso del tramo siempre cumple, que es lo que motivo el cambio: una escala que arranca en
    // 5 y no descuenta con 5 unidades no tiene explicacion para el vendedor.
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(47)]
    public void MultipleCountsFromTheStartOfTheRange(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.True(result.IsSatisfied);
        Assert.Null(result.Code);
        Assert.Equal(0m, result.Shortfall);
    }

    // Y el multiplo crudo dejo de alcanzar: en 5-48 de a 3, 6 y 9 descontaban y ya no. Los dos
    // conjuntos son disjuntos siempre que FromUnit no sea multiplo del paso.
    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(48)]
    public void MultipleRejectsARawMultipleThatDoesNotStartAtTheFloor(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_multiple", result.Code);
    }

    // El ejemplo con el que el developer fijo el criterio: 50-98 de a 6 descuenta en 50, 56, 62
    // ... 98, y no en 54, 60 ni 96, que son justamente los que descontaban antes.
    [Theory]
    [InlineData(50, true)]
    [InlineData(54, false)]
    [InlineData(56, true)]
    [InlineData(96, false)]
    [InlineData(98, true)]
    public void MultipleFollowsTheFloorOfTheScale(decimal quantity, bool satisfied)
    {
        var scale = new QuotationPriceScaleRef(
            50, 98, 5m, QuotationPriceScaleRestriction.Multiple, 6, null);

        Assert.Equal(satisfied, QuotationScaleRestrictionRule.Evaluate(scale, quantity).IsSatisfied);
    }

    // Cuando el piso ya es multiplo del paso las dos lecturas coinciden, y es el caso de la
    // escala sembrada (6-48 de a 3): este cambio no la toca. Vale para todo par donde
    // FromUnit % Multiple == 0.
    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(48)]
    public void AFloorThatIsAlreadyAMultipleBehavesTheSame(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3, fromUnit: 6), quantity);

        Assert.True(result.IsSatisfied);
    }

    // El faltante tambien se cuenta desde el piso: en 5-48 de a 3, a 7 le falta 1 para llegar a
    // 8, no 2 para llegar a 9. Es el numero que la pantalla le muestra al vendedor.
    [Theory]
    [InlineData(6, 2)]
    [InlineData(7, 1)]
    [InlineData(9, 2)]
    [InlineData(10, 1)]
    public void MultipleReportsHowManyUnitsAreMissing(decimal quantity, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_multiple", result.Code);
        Assert.Equal(quantity, result.EvaluatedQuantity);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Evaluate nunca lanza: incumplir el multiplo deja la linea sin descuento, no la bloquea.
    [Fact]
    public void MultipleNeverThrows()
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), 7m);

        Assert.False(result.IsSatisfied);
    }

    // Un multiplo que desmiente la invariante de Catalog no puede bloquear una linea con un
    // dato que nadie corrige desde la cotizacion, ni dividir por cero.
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MultipleIgnoresANonPositiveStep(int multiple)
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(MultipleOf(multiple), 7m).IsSatisfied);
    }

    // La unidad de empaque se cuenta sobre la cantidad cruda, igual que antes: sin cambios.
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(120)]
    public void PackagingUnitAcceptsWholePackages(decimal quantity)
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(12), quantity).IsSatisfied);
    }

    // La unidad de empaque NO se corrio al piso del tramo: sigue contando crudo. Discrimina
    // porque esta escala arranca en 1, asi que con offset 13 y 25 serian validos -- y no forman
    // paquetes enteros de 12.
    [Theory]
    [InlineData(13)]
    [InlineData(25)]
    public void PackagingUnitIsNotCountedFromTheStartOfTheRange(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(12), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_packaging_unit", result.Code);
    }

    // Y dejo de ser un 422 el 2026-09-22: ninguna restriccion de escala bloquea la linea. La
    // cantidad se guarda igual y lo unico que pierde es el descuento de esa escala, que es lo
    // que `Multiple` ya hacia. Queda el codigo, que viaja en la respuesta para que la pantalla
    // pueda explicar por que no descuenta.
    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void PackagingUnitNoLongerBlocksTheLine(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(12), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_packaging_unit", result.Code);
    }

    // Catalog exige un empaque > 0, pero si una fila lo desmiente la linea no pierde su
    // descuento por un dato que nadie corrige desde la cotizacion, y el % de decimal por cero
    // lanza.
    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void PackagingUnitWithoutAUsableSizeDoesNotBlock(int packagingUnit)
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(packagingUnit), 7m).IsSatisfied);
    }

    // Una escala copiada de otro producto llega sin restricción. No hay contra qué evaluarla, y
    // darla por cumplida —lo que hacía el `_ =>` de Evaluate— regalaba su descuento.
    [Fact]
    public void AnIncompleteScaleIsNeverSatisfied()
    {
        var incomplete = new QuotationPriceScaleRef(5, 48, 5m, null, null, null);

        var result = QuotationScaleRestrictionRule.Evaluate(incomplete, 6m);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.product_price_scales_incomplete", result.Code);
    }
}
