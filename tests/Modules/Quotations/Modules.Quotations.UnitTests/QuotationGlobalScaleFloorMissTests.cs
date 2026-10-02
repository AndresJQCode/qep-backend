using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Por qué el piso global no le dio su descuento a una línea. Sin esto el asesor elige "Desde
/// 1000", el porcentaje no se mueve y nada le dice que le faltan unidades para completar el
/// paquete: lo reporta como defecto (2026-09-23).
/// </summary>
public sealed class QuotationGlobalScaleFloorMissTests
{
    // El tramo "de mil" del catálogo real: 35%, paquetes de 50.
    private static readonly QuotationPriceScaleRef ThousandByPackages =
        new(1000, 999_999, 35m, QuotationPriceScaleRestriction.PackagingUnit, null, [50]);

    // El mismo tramo con dos empaques, como la keratina de 120 ml en cajas de 100 y de 150.
    private static readonly QuotationPriceScaleRef ThousandByTwoPackages =
        new(1000, 999_999, 35m, QuotationPriceScaleRestriction.PackagingUnit, null, [100, 150]);

    // El tramo 50 del catálogo real: 20%, de a 6.
    private static readonly QuotationPriceScaleRef FiftyByMultiples =
        new(50, 98, 20m, QuotationPriceScaleRestriction.Multiple, 6, []);

    [Fact]
    public void ALineThatIsNotAWholeNumberOfPackagesSaysHowBigThePackageIs()
    {
        var miss = QuotationGlobalScaleFloorMiss.Explain(
            quantity: 6, discountPercentage: 15m, discountOrigin: "Own",
            scales: [ThousandByPackages], globalScaleFloor: 1000);

        Assert.NotNull(miss);
        Assert.Equal("packaging_unit", miss.Reason);
        Assert.Equal([50], miss.PackagingUnits);
        Assert.Null(miss.Step);
    }

    // Con dos empaques no hay un único tamaño que decir: viaja la lista entera, y Step queda en
    // null para que nadie lo lea como "el" paquete.
    [Fact]
    public void ALineThatCannotBeBuiltFromWholePackagesListsEveryPackage()
    {
        var miss = QuotationGlobalScaleFloorMiss.Explain(
            quantity: 120, discountPercentage: 15m, discountOrigin: "Own",
            scales: [ThousandByTwoPackages], globalScaleFloor: 1000);

        Assert.NotNull(miss);
        Assert.Equal("packaging_unit", miss.Reason);
        Assert.Equal([100, 150], miss.PackagingUnits);
        Assert.Null(miss.Step);
    }

    // 250 = 100 + 150: con los dos empaques se arma, así que no hay nada que explicar.
    [Fact]
    public void ALineBuiltFromACombinationOfPackagesHasNothingToExplain()
    {
        Assert.Null(QuotationGlobalScaleFloorMiss.Explain(
            quantity: 250, discountPercentage: 15m, discountOrigin: "Own",
            scales: [ThousandByTwoPackages], globalScaleFloor: 1000));
    }

    [Fact]
    public void ALineThatIsNotAMultipleSaysTheStep()
    {
        var miss = QuotationGlobalScaleFloorMiss.Explain(
            quantity: 9, discountPercentage: 15m, discountOrigin: "Own",
            scales: [FiftyByMultiples], globalScaleFloor: 50);

        Assert.NotNull(miss);
        Assert.Equal("multiple", miss.Reason);
        Assert.Equal(6, miss.Step);
        Assert.Empty(miss.PackagingUnits);
    }

    [Fact]
    public void AProductWithoutThatTierSaysSo()
    {
        var miss = QuotationGlobalScaleFloorMiss.Explain(
            quantity: 6, discountPercentage: 15m, discountOrigin: "Own",
            scales: [FiftyByMultiples], globalScaleFloor: 1000);

        Assert.NotNull(miss);
        Assert.Equal("no_tier", miss.Reason);
        Assert.Null(miss.Step);
    }

    // Sin piso no hay nada que explicar.
    [Fact]
    public void WithoutAFloorThereIsNothingToExplain()
    {
        Assert.Null(QuotationGlobalScaleFloorMiss.Explain(
            quantity: 6, discountPercentage: 15m, discountOrigin: "Own",
            scales: [ThousandByPackages], globalScaleFloor: null));
    }

    // La línea que tomó el piso no tiene nada que explicar: su porcentaje ya cambió.
    [Fact]
    public void ALineThatTookTheFloorHasNothingToExplain()
    {
        Assert.Null(QuotationGlobalScaleFloorMiss.Explain(
            quantity: 50, discountPercentage: 35m, discountOrigin: "GlobalFloor",
            scales: [ThousandByPackages], globalScaleFloor: 1000));
    }

    // El global sólo reemplaza si mejora. Si la línea ya tenía algo igual o mejor, no "falló":
    // simplemente no hacía falta, y el porcentaje que se ve es el más alto.
    [Fact]
    public void ALineWithAnEqualOrBetterDiscountHasNothingToExplain()
    {
        Assert.Null(QuotationGlobalScaleFloorMiss.Explain(
            quantity: 9, discountPercentage: 20m, discountOrigin: "Own",
            scales: [FiftyByMultiples], globalScaleFloor: 50));
    }

    // Cumple la restricción pero no tomó el piso: lo quitó la compuerta de compra mínima, que ya
    // tiene su propio aviso a nivel cotización. Repetirlo por línea sería ruido, y achacarlo a la
    // restricción sería mentir.
    [Fact]
    public void ALineThatMeetsTheRestrictionButLostItToTheMinimumPurchaseHasNothingToExplain()
    {
        Assert.Null(QuotationGlobalScaleFloorMiss.Explain(
            quantity: 50, discountPercentage: 0m, discountOrigin: "Own",
            scales: [ThousandByPackages], globalScaleFloor: 1000));
    }
}
