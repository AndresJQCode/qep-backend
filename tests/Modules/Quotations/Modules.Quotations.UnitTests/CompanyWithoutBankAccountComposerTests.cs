using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Una empresa sin cuenta (spec 2026-11-17, R3) ya no es un estado de vista previa que se esconde: se guarda y se responde
/// con la empresa y banco y número nulos (la pantalla pinta «Pago: por definir»). Los composers buscan la empresa para su
/// razón social, con o sin cuenta.
/// </summary>
public sealed class CompanyWithoutBankAccountComposerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount WithoutBank = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = null,
        AccountNumber = null,
        Currency = "COP",
        Country = "CO",
    };

    private static readonly QuotationBillingAccount Complete = WithoutBank with
    {
        BankName = "Bancolombia",
        AccountNumber = "12345678",
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
    public async Task AQuotationWithoutBankAnswersTheCompanyWithNullBankFields()
    {
        var (response, lookups) = await ComposeQuotationAsync(WithoutBank);

        Assert.Equal(1, lookups);
        Assert.NotNull(response.BillingAccount);
        Assert.Equal(WithoutBank.CompanyId, response.BillingAccount.CompanyId);
        Assert.Null(response.BillingAccount.BankName);
        Assert.Null(response.BillingAccount.AccountNumber);
    }

    [Fact]
    public async Task AQuotationWithACompleteAccountStillLooksTheCompanyUp()
    {
        var (_, lookups) = await ComposeQuotationAsync(Complete);

        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task AnOrderWithoutBankAnswersTheCompanyWithNullBankFields()
    {
        var (response, lookups) = await ComposeOrderAsync(WithoutBank);

        Assert.Equal(1, lookups);
        Assert.NotNull(response.BillingAccount);
        Assert.Null(response.BillingAccount.BankName);
        Assert.Null(response.BillingAccount.AccountNumber);
    }

    [Fact]
    public async Task AnOrderWithACompleteAccountStillLooksTheCompanyUp()
    {
        var (_, lookups) = await ComposeOrderAsync(Complete);

        Assert.Equal(1, lookups);
    }
}
