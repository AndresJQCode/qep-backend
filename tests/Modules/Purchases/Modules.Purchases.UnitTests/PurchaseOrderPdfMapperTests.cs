using Modules.Purchases.Application;
using Modules.Tenancy.Application;

namespace Modules.Purchases.UnitTests;

/// <summary>Lo que la plantilla del PDF recibe: textos ya formateados con el formato del tenant (D-5 para los confirmados).</summary>
public sealed class PurchaseOrderPdfMapperTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static readonly PurchasesSupplierRef Supplier = new(
        PurchaseOrderTestData.SupplierId, "Textiles Andinos", "NIT", "900123456", true);

    private static TenantRegionalSettings Regional(string numberFormat = "1.234,56", string dateFormat = "dd/MM/yyyy") =>
        new("America/Bogota", dateFormat, "COP", numberFormat);

    private static PurchaseOrderLineDto Line(
        decimal quantity,
        decimal unitCost,
        decimal subtotal,
        decimal? confirmedQuantity = null,
        decimal? confirmedUnitCost = null,
        string lineStatus = "Pending") =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "SKU-A",
            "Camiseta",
            quantity,
            confirmedQuantity,
            unitCost,
            confirmedUnitCost,
            19m,
            lineStatus,
            subtotal);

    private static PurchaseOrderDetailDto Detail(
        string currency,
        IReadOnlyList<PurchaseOrderLineDto> lines,
        PurchaseOrderTotalsDto totals,
        string status = "Draft") =>
        new(
            Guid.CreateVersion7(),
            "OC-0001",
            status,
            false,
            1,
            new PurchaseOrderSupplierDto(PurchaseOrderTestData.SupplierId, "Textiles Andinos", true),
            currency,
            "Entregar en bodega",
            new PurchaseOrderMemberDto(PurchaseOrderTestData.MemberId, "Ana Compras"),
            PurchaseOrderTestData.Now,
            lines,
            totals,
            totals.Total,
            [],
            []);

    [Fact]
    public void MapFormatsMoneyWithTheTenantSeparatorsAndTheCurrencySymbol()
    {
        var cop = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m)),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Today);
        var usd = PurchaseOrderPdfMapper.Map(
            Detail("USD", [Line(1m, 10.5m, 10.5m)], new PurchaseOrderTotalsDto(10.5m, 2m, 12.5m)),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Today);

        Assert.Equal("$ 238", cop.Total);
        Assert.Equal(("$ 200", "$ 38"), (cop.Subtotal, cop.Tax));
        Assert.Equal("$ 100", Assert.Single(cop.Lines).UnitCost);
        Assert.Equal("US$ 12,50", usd.Total);
        Assert.Equal("US$ 10,50", Assert.Single(usd.Lines).UnitCost);
        Assert.Equal(
            ("Mayorista S.A.S.", "OC-0001", "17/11/2026", "Textiles Andinos", "NIT 900123456", "COP", "Entregar en bodega"),
            (cop.TenantName, cop.Number, cop.IssuedOn, cop.SupplierName, cop.SupplierIdentification, cop.Currency, cop.Notes));
    }

    [Fact]
    public void MapFormatsWithTheDotDecimalTenantAndFallsBackToColombiaWithoutSettings()
    {
        var dot = PurchaseOrderPdfMapper.Map(
            Detail("USD", [Line(1500m, 2.5m, 3750m)], new PurchaseOrderTotalsDto(3750m, 712.5m, 4462.5m)),
            Supplier,
            "Mayorista",
            Regional("1,234.56", "yyyy-MM-dd"),
            Today);
        var fallback = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(1500m, 2000m, 3_000_000m)], new PurchaseOrderTotalsDto(3_000_000m, 570_000m, 3_570_000m)),
            Supplier,
            "Mayorista",
            null,
            Today);

        Assert.Equal(("US$ 4,462.50", "2026-11-17", "1,500"), (dot.Total, dot.IssuedOn, Assert.Single(dot.Lines).Quantity));
        Assert.Equal(("$ 3.570.000", "17/11/2026"), (fallback.Total, fallback.IssuedOn));
    }

    // R6 / D-5: con lo confirmado, la línea muestra la cantidad y el costo confirmados y su producto como subtotal.
    [Fact]
    public void MapUsesConfirmedValuesWhenTheLineHasThem()
    {
        var document = PurchaseOrderPdfMapper.Map(
            Detail(
                "COP",
                [Line(3m, 50m, 120m, confirmedQuantity: 2m, confirmedUnitCost: 60m, lineStatus: "Changed")],
                new PurchaseOrderTotalsDto(120m, 22.8m, 142.8m),
                "Confirmed"),
            Supplier,
            "Mayorista",
            Regional(),
            Today);

        var line = Assert.Single(document.Lines);
        Assert.Equal(("2", "$ 60", "$ 120"), (line.Quantity, line.UnitCost, line.Subtotal));
        Assert.Equal(("SKU-A", "Camiseta", "19 %"), (line.Code, line.Name, line.TaxRate));
        Assert.True(document.ShowsConfirmedValues);
    }

    [Theory]
    [InlineData("Draft", "Borrador")]
    [InlineData("Sent", "Enviada")]
    [InlineData("Confirmed", "Confirmada")]
    [InlineData("Received", "Recibida")]
    [InlineData("Cancelled", "Cancelada")]
    public void MapLabelsTheStatusInSpanish(string status, string label)
    {
        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m), status),
            Supplier,
            "Mayorista",
            Regional(),
            Today);

        Assert.Equal(label, document.StatusLabel);
        Assert.False(document.ShowsConfirmedValues);
    }
}
