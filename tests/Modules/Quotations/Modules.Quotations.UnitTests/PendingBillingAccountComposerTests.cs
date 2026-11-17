using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// The composers look the billing company up only to fill the billing block of the response, and a pending account (spec
/// 2026-11-16 withholdings by company, previews only) is never answered there. So a preview with only the company does
/// not query Companies: the lookup would be thrown away. The municipal explanation reads the account's own city, not the
/// company.
/// </summary>
public sealed class PendingBillingAccountComposerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount Pending = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = string.Empty,
        AccountNumber = string.Empty,
        Currency = "COP",
        Country = "CO",
        IsPending = true,
    };

    private static readonly QuotationBillingAccount Complete = Pending with
    {
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        IsPending = false,
    };

    private static StubQuotationCustomerLookup Customers() => new(new QuotationCustomerRef(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static async Task<(QuotationResponse Response, int Lookups)> ComposeQuotationAsync(QuotationBillingAccount account)
    {
        var companies = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>());
        var composer = new QuotationResponseComposer(
            Customers(),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            companies,
            new FixedQuotationSettingsStore(),
            new StubQuotationWithholdingLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            new FixedTenantClock(Now));
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 11, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, account, defaultCurrency: "COP",
            customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None, AdvisorId, Now);

        var response = await composer.ComposeAsync(TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
        return (response, companies.FindCalls);
    }

    private static async Task<(OrderDetailResponse Response, int Lookups)> ComposeOrderAsync(QuotationBillingAccount account)
    {
        var companies = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>());
        var composer = new OrderResponseComposer(
            Customers(),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            companies,
            new StubQuotationWithholdingLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            new StubQuotationBuyerAccountLookup(),
            new FixedTenantClock(Now));
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, []),
            QuotationParties.Empty, account, ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19,
                new QuotationProductSnapshot("ABC", "Aceite"))],
            null, AdvisorId, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        var response = await composer.ComposeAsync(
            TenantId, new OrderDetailDto(order.ToDto(), null, []), TestContext.Current.CancellationToken);
        return (response, companies.FindCalls);
    }

    [Fact]
    public async Task AQuotationWithAPendingAccountDoesNotLookTheCompanyUp()
    {
        var (response, lookups) = await ComposeQuotationAsync(Pending);

        Assert.Equal(0, lookups);
        Assert.Null(response.BillingAccount);
    }

    [Fact]
    public async Task AQuotationWithACompleteAccountStillLooksTheCompanyUp()
    {
        var (_, lookups) = await ComposeQuotationAsync(Complete);

        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task AnOrderWithAPendingAccountDoesNotLookTheCompanyUp()
    {
        var (response, lookups) = await ComposeOrderAsync(Pending);

        Assert.Equal(0, lookups);
        Assert.Null(response.BillingAccount);
    }

    [Fact]
    public async Task AnOrderWithACompleteAccountStillLooksTheCompanyUp()
    {
        var (_, lookups) = await ComposeOrderAsync(Complete);

        Assert.Equal(1, lookups);
    }
}
