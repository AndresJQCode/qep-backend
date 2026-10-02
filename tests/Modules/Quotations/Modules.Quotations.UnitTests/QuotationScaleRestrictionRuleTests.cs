using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleRestrictionRuleTests
{
    private static QuotationPriceScaleRef MultipleOf(int multiple, int fromUnit = 5) =>
        new(fromUnit, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, []);

    // Los empaques son del producto desde el 2026-10-01; la escala los recibe todos.
    private static QuotationPriceScaleRef PackagesOf(params int[] packagingUnits) =>
        new(1, 999_999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnits);

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
            50, 98, 5m, QuotationPriceScaleRestriction.Multiple, 6, []);

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

    // ---- Varios empaques por producto (2026-10-01) ----

    // La keratina de 120 ml viene en cajas de 100 y de 150: vale toda cantidad que se arme con
    // cajas enteras de cualquiera de los dos, mezcladas. 250 = 100 + 150, 300 = 3x100 o 2x150.
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(250)]
    [InlineData(300)]
    [InlineData(1050)]
    public void PackagingUnitsAcceptAnyCombinationOfWholePackages(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(100, 150), quantity);

        Assert.True(result.IsSatisfied);
        Assert.Null(result.Code);
        Assert.Equal(0m, result.Shortfall);
    }

    // El faltante es la distancia a la siguiente cantidad que sí se arma: a 120 le faltan 30
    // para 150, a 260 le faltan 40 para 300 (250 ya quedó atrás), a 50 le faltan 50 para 100.
    [Theory]
    [InlineData(50, 50)]
    [InlineData(120, 30)]
    [InlineData(260, 40)]
    [InlineData(1001, 49)]
    public void PackagingUnitsReportTheDistanceToTheNextCombination(
        decimal quantity, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(100, 150), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_packaging_unit", result.Code);
        Assert.Equal(quantity, result.EvaluatedQuantity);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Con empaques coprimos el hueco se cierra: con 3 y 5 vale todo desde 8, pero 7 no se arma
    // (le falta 1 para 8) y 4 tampoco (le falta 1 para 5). El orden en que llegan no importa.
    [Theory]
    [InlineData(3, true, 0)]
    [InlineData(4, false, 1)]
    [InlineData(7, false, 1)]
    [InlineData(8, true, 0)]
    [InlineData(11, true, 0)]
    public void CoprimePackagingUnitsCloseTheGap(decimal quantity, bool satisfied, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(5, 3), quantity);

        Assert.Equal(satisfied, result.IsSatisfied);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Una cantidad con decimales nunca son cajas enteras. El faltante se mide hasta la siguiente
    // cantidad que sí se arma: de 149,5 a 150 falta 0,5.
    [Fact]
    public void AFractionalQuantityIsNeverAWholeNumberOfPackages()
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(100, 150), 149.5m);

        Assert.False(result.IsSatisfied);
        Assert.Equal(0.5m, result.Shortfall);
    }

    // Un solo empaque se comporta exactamente como antes del cambio, faltante incluido.
    [Theory]
    [InlineData(12, true, 0)]
    [InlineData(13, false, 11)]
    [InlineData(25, false, 11)]
    [InlineData(12.5, false, 11.5)]
    [InlineData(120, true, 0)]
    public void ASinglePackagingUnitBehavesAsBefore(decimal quantity, bool satisfied, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(12), quantity);

        Assert.Equal(satisfied, result.IsSatisfied);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // La cuenta no recorre las cantidades hasta la pedida: trabaja sobre los restos módulo el
    // empaque menor, así que una cantidad enorme cuesta lo mismo que una chica.
    [Fact]
    public void AHugeQuantityIsResolvedWithoutWalkingUpToIt()
    {
        var satisfied = QuotationScaleRestrictionRule.Evaluate(
            PackagesOf(100, 150), 1_000_000_000_050m);
        var missing = QuotationScaleRestrictionRule.Evaluate(
            PackagesOf(100, 150), 1_000_000_000_060m);

        Assert.True(satisfied.IsSatisfied);
        Assert.False(missing.IsSatisfied);
        Assert.Equal(40m, missing.Shortfall);
    }

    // Catalog exige empaques > 0, y no vacíos cuando hay escala de empaque. Si una fila lo
    // desmiente, la línea no pierde su descuento por un dato que nadie corrige desde aquí.
    [Fact]
    public void AnEmptySetOfPackagingUnitsDoesNotBlock()
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(), 7m).IsSatisfied);
    }

    [Fact]
    public void ANonPositivePackagingUnitAmongOthersDoesNotBlock()
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(100, 0), 7m).IsSatisfied);
    }

    // Empaques con divisor común (mcd 2): sumarlos módulo 4 forma dos ciclos de restos, y el
    // impar nunca se alcanza. Se arman 4, 6, 8, 10, 12 …: a 2 le faltan 2 para 4, a 11 le falta
    // 1 para 12 (a 13 le faltaría 1 para 14).
    [Theory]
    [InlineData(2, false, 2)]
    [InlineData(4, true, 0)]
    [InlineData(6, true, 0)]
    [InlineData(10, true, 0)]
    [InlineData(11, false, 1)]
    [InlineData(13, false, 1)]
    public void PackagingUnitsWithACommonDivisorOnlyReachTheirMultiples(
        decimal quantity, bool satisfied, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(4, 6), quantity);

        Assert.Equal(satisfied, result.IsSatisfied);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // El caso clásico de tres empaques (cajas de 6, 9 y 20): 43 es la mayor cantidad que no se
    // arma, así que le falta 1 para 44 (= 20 + 6 × 4), y de ahí en adelante todo se arma.
    [Theory]
    [InlineData(7, false, 2)]
    [InlineData(43, false, 1)]
    [InlineData(44, true, 0)]
    [InlineData(45, true, 0)]
    [InlineData(1_000_001, true, 0)]
    public void ThreePackagingUnitsCloseTheGapAfterTheLargestUnreachableQuantity(
        decimal quantity, bool satisfied, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(6, 9, 20), quantity);

        Assert.Equal(satisfied, result.IsSatisfied);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Una cantidad negativa nunca cumple y no reporta faltante: es el mismo resultado que daba
    // EvaluateStep con un solo empaque, porque no hay "siguiente caja" que tenga sentido.
    [Fact]
    public void ANegativeQuantityIsNotSatisfiedAndHasNoShortfall()
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(100, 150), -5m);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_packaging_unit", result.Code);
        Assert.Equal(0m, result.Shortfall);
    }

    // Por encima de 100000 (el tope de Catalog, Product.MaxPackagingUnitValue) la regla cae al
    // atajo defensivo. Con un solo empaque sigue siendo exacta.
    [Theory]
    [InlineData(100_001, true, 0)]
    [InlineData(100_002, false, 100_000)]
    [InlineData(200_002, true, 0)]
    public void ASinglePackagingUnitAboveTheCatalogLimitIsStillExact(
        decimal quantity, bool satisfied, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(PackagesOf(100_001), quantity);

        Assert.Equal(satisfied, result.IsSatisfied);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Con dos empaques por encima del tope el atajo es a propósito con pérdida: mira cada
    // empaque por separado y no los mezcla. 200004 = 100001 + 100003 sí se arma, pero el atajo
    // lo niega y dice que faltan 2 para 200006 (2 × 100003). Nunca regala un descuento; sólo
    // puede negar uno, y Catalog no deja cargar empaques así.
    [Fact]
    public void TwoPackagingUnitsAboveTheCatalogLimitAreIntentionallyNotCombined()
    {
        var result = QuotationScaleRestrictionRule.Evaluate(
            PackagesOf(100_001, 100_003), 200_004m);

        Assert.False(result.IsSatisfied);
        Assert.Equal(2m, result.Shortfall);
    }

    // El piso global evalúa desde cero, y el empaque ya contaba crudo: las dos lecturas
    // coinciden también con varios empaques.
    [Theory]
    [InlineData(120)]
    [InlineData(250)]
    [InlineData(260)]
    public void EvaluateFromZeroMatchesEvaluateForPackagingUnits(decimal quantity)
    {
        var scale = PackagesOf(100, 150);

        Assert.Equal(
            QuotationScaleRestrictionRule.Evaluate(scale, quantity),
            QuotationScaleRestrictionRule.EvaluateFromZero(scale, quantity));
    }

    // Una escala copiada de otro producto llega sin restricción. No hay contra qué evaluarla, y
    // darla por cumplida —lo que hacía el `_ =>` de Evaluate— regalaba su descuento.
    [Fact]
    public void AnIncompleteScaleIsNeverSatisfied()
    {
        var incomplete = new QuotationPriceScaleRef(5, 48, 5m, null, null, []);

        var result = QuotationScaleRestrictionRule.Evaluate(incomplete, 6m);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.product_price_scales_incomplete", result.Code);
    }
}
