using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El mapeo de una fila del historico al DTO del reporte: lo unico que Application agrega sobre
/// lo que el adaptador lee de la tabla.
/// </summary>
public sealed class PriceChangeReportMappingTests
{
    private static readonly DateTimeOffset ChangedAt =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CarriesTheRowThroughAndComputesTheDifference()
    {
        var dto = Row(PriceChangeField.PriceBase, 1000m, 1200m).ToDto();

        Assert.Equal("PriceBase", dto.Field);
        Assert.Equal(1000m, dto.PreviousValue);
        Assert.Equal(1200m, dto.NewValue);
        Assert.Equal(200m, dto.Difference);
        Assert.Equal("Vela de soja", dto.ProductName);
        Assert.Equal("VS-001", dto.ProductCode);
        Assert.Equal("asesora@qcode.co", dto.ChangedByName);
        Assert.Equal(ChangedAt, dto.ChangedAt);
    }

    /// <summary>El contrato dice que el rango viene con valor **solo** cuando el campo es
    /// <c>ScaleDiscount</c>. Es una afirmacion sobre la respuesta, asi que el recorte se hace en
    /// el mapeo y no se confia en que la tabla venga limpia.</summary>
    [Fact]
    public void DropsTheScaleRangeForABasePriceChange()
    {
        var row = Row(PriceChangeField.PriceBase, 100m, 120m) with
        {
            ScaleFromUnit = 1,
            ScaleToUnit = 9
        };

        var dto = row.ToDto();

        Assert.Null(dto.ScaleFromUnit);
        Assert.Null(dto.ScaleToUnit);
    }

    [Fact]
    public void KeepsTheScaleRangeForAScaleDiscountChange()
    {
        var row = Row(PriceChangeField.ScaleDiscount, 10m, 25m) with
        {
            ScaleFromUnit = 1,
            ScaleToUnit = 9
        };

        var dto = row.ToDto();

        Assert.Equal("ScaleDiscount", dto.Field);
        Assert.Equal(1, dto.ScaleFromUnit);
        Assert.Equal(9, dto.ScaleToUnit);
        Assert.Equal(15m, dto.Difference);
    }

    [Fact]
    public void ABasePriceRowKeepsItsCurrencyAndADiscountRowHasNone()
    {
        var basePrice = (Row(PriceChangeField.PriceBase, 10m, 12m) with { Currency = "EUR" }).ToDto();
        // The contract describes the response, not the table: a discount row never shows a currency.
        var discount = (Row(PriceChangeField.ScaleDiscount, 10m, 12m) with { Currency = "EUR" }).ToDto();

        Assert.Equal(("PriceBase", "EUR"), (basePrice.Field, basePrice.Currency));
        Assert.Equal(("ScaleDiscount", (string?)null), (discount.Field, discount.Currency));
    }

    [Fact]
    public void MapsEveryRowOfThePage()
    {
        IReadOnlyList<PriceChangeReportRow> rows =
        [
            Row(PriceChangeField.PriceBase, null, 90m),
            Row(PriceChangeField.PriceBase, 400_000m, null)
        ];

        var dtos = rows.ToDtos();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(90m, dtos[0].Difference);
        Assert.Equal(-400_000m, dtos[1].Difference);
    }

    private static PriceChangeReportRow Row(
        PriceChangeField field,
        decimal? previousValue,
        decimal? newValue) =>
        new(
            Guid.Parse("01900000-0000-7000-8000-0000000000a1"),
            Guid.Parse("01900000-0000-7000-8000-0000000000a2"),
            "VS-001",
            "Vela de soja",
            field,
            field == PriceChangeField.PriceBase ? "COP" : null,
            ScaleFromUnit: null,
            ScaleToUnit: null,
            previousValue,
            newValue,
            Guid.Parse("01900000-0000-7000-8000-0000000000a3"),
            "asesora@qcode.co",
            ChangedAt);
}
