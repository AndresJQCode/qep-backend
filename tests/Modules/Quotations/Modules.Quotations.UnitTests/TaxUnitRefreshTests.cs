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

    private static Quotation NewQuotation(MunicipalContext? municipal = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, ColombianAccount, "COP", customerVatSurplus: false,
            customerWithholdings: [RetefuenteServicios], municipal ?? MunicipalContext.None, AdvisorId, Now);
        // 226.100 con el IVA del 19 % adentro: base sin IVA 190.000.
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 226_100m, 0m, 19, AdvisorId, Now);
        return quotation;
    }

    private static Order NewOrder() =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, [RetefuenteServicios]),
            QuotationParties.Empty, ColombianAccount, ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 226_100m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            null, AdvisorId, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

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
}
