using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec alerta UVT §5.3: la vista previa de la conversión compara el pedido tal como lo arma la foto de la cotización
/// («antes») con el mismo pedido con la UVT del día («después»). El backend decide qué cambió; el frontend no resta montos.
/// </summary>
public sealed class QuotationConversionPreviewTests
{
    private static readonly Guid RateId = Guid.CreateVersion7();

    [Fact]
    public void CompareIsNullWhenNoTaxUnitChanged()
    {
        var rows = new[] { Row(2026, 50_000m, applies: true, amount: 4_000m) };

        Assert.Null(QuotationConversionPreview.Compare(rows, 115_000m, rows, 115_000m));
    }

    // 2 × 50.000 = 100.000: la base de 100.000 aplica (4.000). Con 2 × 60.000 = 120.000 ya no.
    [Fact]
    public void CompareReportsTheRowThatStopsApplying()
    {
        var before = new[] { Row(2025, 50_000m, applies: true, amount: 4_000m) };
        var after = new[] { Row(2026, 60_000m, applies: false, amount: 0m) };

        var change = QuotationConversionPreview.Compare(before, 115_000m, after, 119_000m);

        Assert.NotNull(change);
        Assert.Equal([new TaxUnitShiftDto("CO", 2025, 50_000m, 2026, 60_000m)], change.TaxUnits);
        var row = Assert.Single(change.Withholdings);
        Assert.Equal((true, false, 4_000m, 0m), (row.FromApplies, row.ToApplies, row.FromAmount, row.ToAmount));
        Assert.Equal(
            (RateId, "Retefuente servicios 4 %", "Retefuente", (decimal?)2m, "UVT", (decimal?)100_000m, (decimal?)120_000m, 100_000m),
            (row.TaxRateId, row.Name, row.Type, row.MinimumBase, row.MinimumBaseUnit, row.FromMinimumBaseAmount,
                row.ToMinimumBaseAmount, row.Base));
        Assert.Equal((115_000m, 119_000m), (change.FromNetTotal, change.ToNetTotal));
    }

    // La UVT cambia pero la base sigue lejos del umbral: hay cambio de UVT y ninguna fila que mostrar.
    [Fact]
    public void CompareOmitsARowWhoseAmountAndVerdictDidNotChange()
    {
        var before = new[] { Row(2025, 50_000m, applies: true, amount: 4_000m) };
        var after = new[] { Row(2026, 60_000m, applies: true, amount: 4_000m) };

        var change = QuotationConversionPreview.Compare(before, 115_000m, after, 115_000m);

        Assert.NotNull(change);
        Assert.Single(change.TaxUnits);
        Assert.Empty(change.Withholdings);
    }

    // Una fila anterior a la UVT con año (spec retenciones por UVT §8.2) trae valor y no año.
    [Fact]
    public void CompareReportsALegacyRowWithoutYear()
    {
        var before = new[] { Row(null, 50_000m, applies: true, amount: 4_000m) };
        var after = new[] { Row(2026, 60_000m, applies: false, amount: 0m) };

        var change = QuotationConversionPreview.Compare(before, 115_000m, after, 119_000m);

        Assert.NotNull(change);
        Assert.Equal([new TaxUnitShiftDto("CO", null, 50_000m, 2026, 60_000m)], change.TaxUnits);
    }

    // Las filas se emparejan por posición: dos desgloses de largo distinto no salen de la misma cotización, y emparejarlos
    // con Zip descartaría filas en silencio. Es un error de programación, no un error de dominio (spec alerta UVT §5.3).
    [Fact]
    public void CompareThrowsWhenTheBreakdownsHaveDifferentLengths()
    {
        var before = new[] { Row(2025, 50_000m, applies: true, amount: 4_000m) };
        var after = new[] { Row(2026, 60_000m, applies: false, amount: 0m), Row(2026, 60_000m, applies: false, amount: 0m) };

        Assert.Throws<InvalidOperationException>(() => QuotationConversionPreview.Compare(before, 115_000m, after, 119_000m));
    }

    // Mismo largo pero otra tasa en la misma posición: compararlas mezclaría el monto de una retención con el de otra.
    [Fact]
    public void CompareThrowsWhenARowPairsDifferentTaxRates()
    {
        var before = new[] { Row(2025, 50_000m, applies: true, amount: 4_000m) };
        var after = new[] { Row(2026, 60_000m, applies: false, amount: 0m) with { TaxRateId = Guid.CreateVersion7() } };

        Assert.Throws<InvalidOperationException>(() => QuotationConversionPreview.Compare(before, 115_000m, after, 119_000m));
    }

    private static WithholdingVerdictDto Row(int? year, decimal value, bool applies, decimal amount) => new(
        RateId, "Retefuente servicios 4 %", "Retefuente", "CO", 4m, 4m, false, null, applies,
        applies ? "Applied" : "BelowMinimumBase", 100_000m, 2m, "UVT", 2m * value, value, year, amount);
}
