using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleRestrictionRuleTests
{
    private static QuotationPriceScaleRef MultipleOf(int multiple, int fromUnit = 5) =>
        new(fromUnit, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, null);

    private static QuotationPriceScaleRef PackagesOf(int packagingUnit) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    // El multiplo se cuenta desde FromUnit, no desde cero.
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(47)]
    public void MultipleAcceptsQuantitiesOnTheStep(decimal quantity)
    {
        QuotationScaleRestrictionRule.EnsureSatisfied(MultipleOf(3), quantity);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(7.5)]
    public void MultipleRejectsQuantitiesOffTheStep(decimal quantity)
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsureSatisfied(MultipleOf(3), quantity));

        Assert.Equal("quotation.item.quantity_not_multiple", exception.Code);
    }

    // La unidad de empaque se cuenta sobre la cantidad cruda, no sobre el excedente.
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(120)]
    public void PackagingUnitAcceptsWholePackages(decimal quantity)
    {
        QuotationScaleRestrictionRule.EnsureSatisfied(PackagesOf(12), quantity);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    [InlineData(25)]
    public void PackagingUnitRejectsPartialPackages(decimal quantity)
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsureSatisfied(PackagesOf(12), quantity));

        Assert.Equal("quotation.item.quantity_not_packaging_unit", exception.Code);
    }

    // El mensaje lo lee la pantalla para decir "de a 3 desde 5": sin los numeros no sirve.
    [Fact]
    public void MultipleMessageCarriesTheStepAndItsOrigin()
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsureSatisfied(MultipleOf(3), 7m));

        Assert.Contains("3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("5", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingUnitMessageCarriesThePackageSize()
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsureSatisfied(PackagesOf(12), 20m));

        Assert.Contains("12", exception.Message, StringComparison.Ordinal);
    }

    // Catalog garantiza que el campo de la restriccion viene poblado y es > 0. Si una fila lo
    // desmiente, la linea no se bloquea con un dato que nadie puede corregir desde la cotizacion
    // -- y sobre todo no se divide por cero.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MultipleWithoutAUsableStepDoesNotBlock(int? multiple)
    {
        QuotationScaleRestrictionRule.EnsureSatisfied(
            new(5, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, null), 7m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void PackagingUnitWithoutAUsableSizeDoesNotBlock(int? packagingUnit)
    {
        QuotationScaleRestrictionRule.EnsureSatisfied(
            new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit), 7m);
    }
}
