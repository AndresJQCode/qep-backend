using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleRestrictionRuleTests
{
    private static QuotationPriceScaleRef MultipleOf(int multiple, int fromUnit = 5) =>
        new(fromUnit, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, null);

    private static QuotationPriceScaleRef PackagesOf(int packagingUnit) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    // El multiplo se cuenta sobre la cantidad cruda, no desde FromUnit. Revierte el criterio de
    // 5a76b07: en una escala 5-48 de a 3, 8 unidades ya no cumple (8 - 5 = 3 daba valido).
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(48)]
    public void MultipleAcceptsRawMultiples(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.True(result.IsSatisfied);
        Assert.Null(result.Code);
        Assert.Equal(0m, result.Shortfall);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(7, 2)]
    [InlineData(8, 1)]
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
        QuotationScaleRestrictionRule.EnsurePackagingUnit(PackagesOf(12), quantity);
    }

    // Y sigue siendo un 422: su comportamiento no lo toca esta funcionalidad.
    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void PackagingUnitStillThrows(decimal quantity)
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsurePackagingUnit(PackagesOf(12), quantity));

        Assert.Equal("quotation.item.quantity_not_packaging_unit", exception.Code);
    }

    // Catalog exige un empaque > 0, pero si una fila lo desmiente el guard tiene que sostener el
    // caso desde EnsurePackagingUnit, que es el unico camino que la produccion llama: el % de
    // decimal por cero lanza, y una linea no se bloquea con un dato que nadie corrige desde la
    // cotizacion.
    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void PackagingUnitWithoutAUsableSizeDoesNotBlock(int packagingUnit)
    {
        QuotationScaleRestrictionRule.EnsurePackagingUnit(PackagesOf(packagingUnit), 7m);
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(packagingUnit), 7m).IsSatisfied);
    }
}
