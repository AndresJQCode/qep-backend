using Modules.Purchases.Application;
using Modules.Tenancy.Application;

namespace Modules.Purchases.UnitTests;

/// <summary>Lo que la plantilla del PDF recibe: textos ya formateados con el formato del tenant (D-5 para los confirmados).</summary>
public sealed class PurchaseOrderPdfMapperTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static readonly PurchasesSupplierRef Supplier = new(
        PurchaseOrderTestData.SupplierId, "Textiles Andinos", "NIT", "900123456", true);

    private static TenantRegionalSettings Regional(string numberFormat = "1.234,56", string dateFormat = "dd/MM/yyyy") =>
        new("America/Bogota", dateFormat, "COP", numberFormat);

    private static PurchaseOrderLineDto Line(
        decimal quantity,
        decimal? unitCost,
        decimal? subtotal,
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
        PurchaseOrderTotalsDto? totals,
        string status = "Draft",
        DateTimeOffset? createdAt = null,
        IReadOnlyList<PurchaseOrderHistoryDto>? history = null) =>
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
            createdAt ?? PurchaseOrderTestData.Now,
            lines,
            totals,
            totals?.Total,
            history ?? [],
            [],
            ShowPrices: totals is not null);

    [Fact]
    public void MapFormatsMoneyWithTheTenantSeparatorsAndTheCurrencySymbol()
    {
        var cop = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m)),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Bogota);
        var usd = PurchaseOrderPdfMapper.Map(
            Detail("USD", [Line(1m, 10.5m, 10.5m)], new PurchaseOrderTotalsDto(10.5m, 2m, 12.5m)),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Bogota);

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
    public void MapPrintsADashWhereAnOrderWithoutPricesHasNoAmount()
    {
        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, null, null)], null),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Bogota);

        var line = Assert.Single(document.Lines);
        Assert.Equal(("2", "—", "—"), (line.Quantity, line.UnitCost, line.Subtotal));
        Assert.Equal("19 %", line.TaxRate);
        Assert.Equal(("—", "—", "—"), (document.Subtotal, document.Tax, document.Total));
        Assert.False(document.ShowsAmounts);
    }

    [Fact]
    public void MapKeepsShowingAmountsForAPricedOrder()
    {
        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m)),
            Supplier,
            "Mayorista S.A.S.",
            Regional(),
            Bogota);

        Assert.True(document.ShowsAmounts);
    }

    [Fact]
    public void MapFormatsWithTheDotDecimalTenantAndFallsBackToColombiaWithoutSettings()
    {
        var dot = PurchaseOrderPdfMapper.Map(
            Detail("USD", [Line(1500m, 2.5m, 3750m)], new PurchaseOrderTotalsDto(3750m, 712.5m, 4462.5m)),
            Supplier,
            "Mayorista",
            Regional("1,234.56", "yyyy-MM-dd"),
            Bogota);
        var fallback = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(1500m, 2000m, 3_000_000m)], new PurchaseOrderTotalsDto(3_000_000m, 570_000m, 3_570_000m)),
            Supplier,
            "Mayorista",
            null,
            Bogota);

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
            Bogota);

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
            Bogota);

        Assert.Equal(label, document.StatusLabel);
        Assert.False(document.ShowsConfirmedValues);
    }

    // R11: el PDF muestra la fecha de la orden, no la del día en que se descarga. En borrador, la del alta.
    [Fact]
    public void ADraftIsDatedByItsCreation()
    {
        var created = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m), "Draft", created,
                [new PurchaseOrderHistoryDto("Draft", created, "Ana Compras")]),
            Supplier,
            "Mayorista",
            Regional(),
            Bogota);

        Assert.Equal("03/10/2026", document.IssuedOn);
    }

    // R11: una vez enviada, la fecha es la del envío, también después de confirmar, recibir o cancelar.
    [Theory]
    [InlineData("Sent")]
    [InlineData("Confirmed")]
    [InlineData("Received")]
    [InlineData("Cancelled")]
    public void AnOrderSentIsDatedByTheSendTransition(string status)
    {
        var created = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
        var sent = new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
        var history = new List<PurchaseOrderHistoryDto>
        {
            new("Draft", created, "Ana Compras"),
            new("Sent", sent, "Ana Compras"),
        };
        if (status != "Sent")
        {
            history.Add(new PurchaseOrderHistoryDto(status, sent.AddDays(5), "Ana Compras"));
        }

        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m), status, created, history),
            Supplier,
            "Mayorista",
            Regional(),
            Bogota);

        Assert.Equal("08/10/2026", document.IssuedOn);
    }

    // R11: una orden cancelada sin haberse enviado sigue con la fecha del alta.
    [Fact]
    public void AnOrderCancelledFromDraftKeepsItsCreationDate()
    {
        var created = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m), "Cancelled", created,
                [new("Draft", created, "Ana Compras"), new("Cancelled", created.AddDays(2), "Ana Compras")]),
            Supplier,
            "Mayorista",
            Regional(),
            Bogota);

        Assert.Equal("03/10/2026", document.IssuedOn);
    }

    // R11: el día es el del tenant. 02:00 UTC del 9 es todavía el 8 en Bogotá (UTC-5).
    [Fact]
    public void TheDateIsTheTenantsLocalDay()
    {
        var created = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
        var sent = new DateTimeOffset(2026, 10, 9, 2, 0, 0, TimeSpan.Zero);

        var document = PurchaseOrderPdfMapper.Map(
            Detail("COP", [Line(2m, 100m, 200m)], new PurchaseOrderTotalsDto(200m, 38m, 238m), "Sent", created,
                [new("Draft", created, "Ana Compras"), new("Sent", sent, "Ana Compras")]),
            Supplier,
            "Mayorista",
            Regional(),
            Bogota);

        Assert.Equal("08/10/2026", document.IssuedOn);
    }
}
