using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El refresco de la UVT en documentos editables (spec retenciones por UVT §5.4, owner P1): cada fila con base mínima en
/// unidades toma el valor y el año vigentes y el documento se recalcula; las demás filas y los documentos cerrados no
/// cambian.
/// </summary>
public sealed class TaxUnitRefreshTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Bogota = Guid.CreateVersion7();

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    // 4 UVT x 49.799 = 199.196: con 190.000 de base no aplica.
    private static readonly WithholdingRate RetefuenteServicios =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, 4m, "UVT", 49_799m)
        {
            TaxUnitYear = 2025,
        };

    private static readonly WithholdingRate ReteIca = Rate("ReteICA Bogotá", "ReteICA", 0.966m, Bogota);

    private static readonly Dictionary<string, TaxUnitPhoto> Uvt2026 = new() { ["CO"] = new TaxUnitPhoto(47_000m, 2026) };

    // 4 UVT x 51.000 = 204.000: sube el umbral por encima de una base de 200.000.
    private static readonly Dictionary<string, TaxUnitPhoto> Uvt51000 = new() { ["CO"] = new TaxUnitPhoto(51_000m, 2026) };

    // 226.100 con el IVA del 19 % adentro: base sin IVA 190.000.
    private static Quotation NewQuotation(MunicipalContext? municipal = null, decimal unitPrice = 226_100m)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, ColombianAccount, "COP", customerVatSurplus: false,
            customerWithholdings: [RetefuenteServicios], municipal ?? MunicipalContext.None, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, unitPrice, 0m, 19, AdvisorId, Now);
        return quotation;
    }

    private static Order FromQuotation(
        Quotation quotation,
        IReadOnlyDictionary<string, TaxUnitPhoto>? taxUnits,
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null) =>
        Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, null, AdvisorId, proofs ?? [], Now, taxUnits);

    private static Order NewOrder() =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, [RetefuenteServicios]),
            QuotationParties.Empty, ColombianAccount, ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 226_100m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            null, AdvisorId, [], Now);

    [Fact]
    public void RefreshUpdatesValueAndYearAndRecalculates()
    {
        var quotation = NewQuotation();
        Assert.Equal(0m, quotation.RetentionAmount);

        quotation.RefreshTaxUnits(Uvt2026);

        var row = Assert.Single(quotation.CustomerWithholdings);
        Assert.Equal(((decimal?)47_000m, (int?)2026), (row.TaxUnitValue, row.TaxUnitYear));
        Assert.Equal(188_000m, row.Rate.Threshold);
        Assert.True(row.Applied);
        Assert.Equal(7_600m, quotation.RetentionAmount);
    }

    [Fact]
    public void RefreshWithoutTheCountryLeavesTheUnitEmpty()
    {
        var quotation = NewQuotation();

        quotation.RefreshTaxUnits(new Dictionary<string, TaxUnitPhoto>());

        var row = Assert.Single(quotation.CustomerWithholdings);
        Assert.Equal(((decimal?)null, (int?)null), (row.TaxUnitValue, row.TaxUnitYear));
        Assert.False(row.Applied);
        Assert.Equal(0m, quotation.RetentionAmount);
    }

    [Fact]
    public void RefreshLeavesARateWithoutMinimumBaseAlone()
    {
        var quotation = NewQuotation(
            new MunicipalContext(Bogota, [new MunicipalOffer("ReteICA", ReteIca, CustomerIsAgent: true)]));

        quotation.RefreshTaxUnits(Uvt2026);

        var reteIca = Assert.Single(quotation.CustomerWithholdings, row => row.Type == "ReteICA");
        Assert.Equal(((decimal?)null, (int?)null), (reteIca.TaxUnitValue, reteIca.TaxUnitYear));
        Assert.Equal(ReteIca, reteIca.Rate);
    }

    [Fact]
    public void RefreshThrowsOnANonEditableQuotation()
    {
        var quotation = NewQuotation();
        quotation.ConvertToOrder(AdvisorId, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Aceite"),
        });

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.RefreshTaxUnits(Uvt2026));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
        Assert.Equal((int?)2025, Assert.Single(quotation.CustomerWithholdings).TaxUnitYear);
    }

    [Fact]
    public void RefreshUpdatesAnEditableOrder()
    {
        var order = NewOrder();

        order.RefreshTaxUnits(Uvt2026);

        var row = Assert.Single(order.Withholdings);
        Assert.Equal(((decimal?)47_000m, (int?)2026), (row.TaxUnitValue, row.TaxUnitYear));
        Assert.Equal(7_600m, order.RetentionAmount);
    }

    [Fact]
    public void RefreshThrowsOnANonEditableOrder()
    {
        var order = NewOrder();
        order.Invoice(AdvisorId, requireFullPayment: false, Now);

        var error = Assert.Throws<QuotationsDomainException>(() => order.RefreshTaxUnits(Uvt2026));

        Assert.Equal(ExternalOrderErrors.NotEditable, error.Code);
        Assert.Equal((int?)2025, Assert.Single(order.Withholdings).TaxUnitYear);
    }

    // Spec alerta UVT §5.2: el pedido nace con la UVT de su año, y sus totales ya la usan.
    [Fact]
    public void FromQuotationWithTaxUnitsRefreshesBeforeTotals()
    {
        var quotation = NewQuotation();
        Assert.Equal(0m, quotation.RetentionAmount);

        var order = FromQuotation(quotation, Uvt2026);

        var row = Assert.Single(order.Withholdings);
        Assert.Equal(((int?)2026, (decimal?)47_000m), (row.TaxUnitYear, row.TaxUnitValue));
        Assert.Equal((226_100m, 7_600m, 218_500m), (order.Total, order.RetentionAmount, order.NetTotal));
    }

    [Fact]
    public void FromQuotationWithoutTaxUnitsKeepsThePhoto()
    {
        var quotation = NewQuotation();

        var order = FromQuotation(quotation, taxUnits: null);

        var row = Assert.Single(order.Withholdings);
        Assert.Equal(((int?)2025, (decimal?)49_799m), (row.TaxUnitYear, row.TaxUnitValue));
        Assert.Equal(
            (quotation.Total, quotation.RetentionAmount, quotation.NetTotal),
            (order.Total, order.RetentionAmount, order.NetTotal));
    }

    // Con la foto congelada la retención aplica (base 200.000 sobre 199.196) y el comprobante cubre el neto; con la UVT
    // nueva deja de aplicar, el neto sube a 238.000 y el mismo comprobante ya no alcanza.
    [Fact]
    public void FromQuotationComputesPaymentStatusAgainstTheRefreshedNet()
    {
        var quotation = NewQuotation(unitPrice: 238_000m);
        Assert.Equal((8_000m, 230_000m), (quotation.RetentionAmount, quotation.NetTotal));

        var order = FromQuotation(
            quotation, Uvt51000, [new OrderPaymentProofInput(Guid.CreateVersion7(), quotation.NetTotal)]);

        Assert.Equal((0m, 238_000m), (order.RetentionAmount, order.NetTotal));
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
    }

    [Fact]
    public void FromQuotationRefreshKeepsCashOnDeliveryPending()
    {
        var quotation = NewQuotation();
        quotation.SetShipping(new ShippingDetails(null, null, null, 0m, 0, true), AdvisorId, Now);

        var order = FromQuotation(quotation, Uvt2026);

        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
        Assert.Equal((int?)2026, Assert.Single(order.Withholdings).TaxUnitYear);
    }

    // Spec alerta UVT §5.2: sin ninguna fila con base en unidades la conversión no lee Catalog.
    [Fact]
    public async Task UnitsForWithoutUnitRowsDoesNotQueryTheLookup()
    {
        var quotation = NewQuotation(
            new MunicipalContext(Bogota, [new MunicipalOffer("ReteICA", ReteIca, CustomerIsAgent: true)]));
        var lookup = new StubQuotationWithholdingLookup().WithTaxUnit("CO", 47_000m, 2026);

        var units = await TaxUnitRefresh.UnitsForAsync(
            quotation.Withholdings.Where(row => row.Type == "ReteICA"), lookup, new DateOnly(2026, 11, 16),
            TestContext.Current.CancellationToken);

        Assert.Null(units);
        Assert.Empty(lookup.TaxUnitRequests);
    }
}
