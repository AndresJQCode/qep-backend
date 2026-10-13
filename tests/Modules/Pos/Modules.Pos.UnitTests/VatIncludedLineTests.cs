using System.Globalization;
using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.UnitTests;

/// <summary>
/// La fórmula de QuotationItem.Apply, extraída sin cambios (spec 2026-10-07, «Fórmula de línea
/// compartida»). Los tres primeros casos son el ejemplo trabajado del spec, al centavo.
/// </summary>
public sealed class VatIncludedLineTests
{
    [Theory]
    [InlineData("2", "11900", "10", 19, "2380.00", "3420.00", "18000.00")]
    [InlineData("1.5", "5000", "0", 0, "0", "0", "7500.00")]
    [InlineData("3", "2990", "0", 5, "0", "427.14", "8542.86")]
    // Descuento del 100 %: la línea queda en cero y no hay IVA que extraer.
    [InlineData("2", "11900", "100", 19, "23800.00", "0", "0")]
    // Cantidad con dos decimales y descuento con centavos: 3737.5 bruto, 261.625 de descuento.
    [InlineData("1.25", "2990", "7", 19, "261.63", "554.97", "2920.90")]
    public void ComputeMatchesTheQuotationFormula(
        string quantity, string unitPrice, string discount, int tax,
        string expectedDiscount, string expectedTax, string expectedSubtotal)
    {
        var amounts = VatIncludedLine.Compute(
            decimal.Parse(quantity, CultureInfo.InvariantCulture),
            decimal.Parse(unitPrice, CultureInfo.InvariantCulture),
            decimal.Parse(discount, CultureInfo.InvariantCulture),
            tax);

        Assert.Equal(decimal.Parse(expectedDiscount, CultureInfo.InvariantCulture), amounts.DiscountAmount);
        Assert.Equal(decimal.Parse(expectedTax, CultureInfo.InvariantCulture), amounts.TaxAmount);
        Assert.Equal(decimal.Parse(expectedSubtotal, CultureInfo.InvariantCulture), amounts.Subtotal);
    }

    // AwayFromZero y no ToEven: 0.5 × 0.25 = 0.125 tiene que dar 0.13. Con el redondeo bancario
    // de .NET por defecto daría 0.12 y el ticket no coincidiría con la cotización.
    [Fact]
    public void ThePointFiveCentRoundsAwayFromZero()
    {
        var amounts = VatIncludedLine.Compute(0.5m, 0.25m, 0m, 0);

        Assert.Equal(0.13m, amounts.Subtotal);
        Assert.Equal(0.13m, amounts.LineTotal);
    }

    [Fact]
    public void LineTotalIsSubtotalPlusTax()
    {
        var amounts = VatIncludedLine.Compute(2m, 11_900m, 10m, 19);

        Assert.Equal(21_420m, amounts.LineTotal);
    }

    // Fix 2026-10-14: en COP el unitario descontado se cobra en pesos enteros, igual en la cotización
    // y en POS. La facturación masiva le manda a Siigo round("Valor Unit" × 1,19) × cantidad, y sólo
    // con el unitario entero eso da lo mismo que se cobró. 100 kits 7703 a 35.000 con 33,33%:
    // 23.334,50 c/u se cobra 23.335, la línea 2.333.500 y el descuento lo que falta del bruto.
    [Fact]
    public void InPesosTheDiscountedUnitPriceIsChargedInWholePesos()
    {
        var amounts = VatIncludedLine.Compute(100m, 35_000m, 33.33m, 19, "COP");

        Assert.Equal(2_333_500m, amounts.LineTotal);
        Assert.Equal(1_166_500m, amounts.DiscountAmount);
        Assert.Equal(372_575.63m, amounts.TaxAmount);
        Assert.Equal(1_960_924.37m, amounts.Subtotal);
    }

    // Un precio de lista con centavos también se cobra entero, sin descuento negativo.
    [Fact]
    public void InPesosAListPriceWithCentsIsChargedWholeWithoutANegativeDiscount()
    {
        var amounts = VatIncludedLine.Compute(100m, 23_334.50m, 0m, 19, "COP");

        Assert.Equal(2_333_500m, amounts.LineTotal);
        Assert.Equal(0m, amounts.DiscountAmount);
        Assert.Equal(23_335m, VatIncludedLine.ChargedListPrice(23_334.50m, "COP"));
    }

    // En otra moneda los centavos son plata: el resultado es el de siempre.
    [Fact]
    public void InAnotherCurrencyTheCentsStay()
    {
        Assert.Equal(
            VatIncludedLine.Compute(100m, 24.63m, 5m, 19),
            VatIncludedLine.Compute(100m, 24.63m, 5m, 19, "USD"));
        Assert.Equal(24.63m, VatIncludedLine.ChargedListPrice(24.63m, "USD"));
    }
}
