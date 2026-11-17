using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La cuenta de cobro opcional (spec 2026-11-17 §4.2, R3): una empresa sin cuenta es banco y número nulos, un estado que se
/// guarda. Enviar y convertir exigen la empresa, no la cuenta, y los cuatro caminos que crean un pedido la aceptan.
/// </summary>
public sealed class OptionalBillingAccountTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static QuotationBillingAccount WithoutBank(string currency = "COP") => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = null,
        AccountNumber = null,
        Currency = currency,
        Country = "CO",
    };

    private static QuotationBillingAccount WithBank(string currency = "COP") => WithoutBank(currency) with
    {
        BankName = "Bancolombia",
        AccountNumber = "12345678",
    };

    private static OrderHeader Header => new(ClientId, Advisor, "COP", null, false, null, false, []);

    private static OrderLineInput Line =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Quotation NewQuotation(QuotationBillingAccount? account, string defaultCurrency = "COP")
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, Today.AddDays(15), null, null,
            QuotationParties.Empty, account, defaultCurrency, customerVatSurplus: false, [], MunicipalContext.None, Advisor, Now);
        return quotation;
    }

    [Fact]
    public void ACompanyWithoutBankAccountNormalizesBothFieldsToNull()
    {
        var account = WithoutBank() with { BankName = "  ", AccountNumber = null };

        var normalized = account.Normalized();

        Assert.Null(normalized.BankName);
        Assert.Null(normalized.AccountNumber);
        Assert.False(normalized.HasBankAccount);
    }

    [Fact]
    public void OnlyTheBankNameIsAccountNumberRequired()
    {
        var account = WithoutBank() with { BankName = "Bancolombia" };

        var exception = Assert.Throws<QuotationsDomainException>(() => account.Normalized());

        Assert.Equal("quotation.billing.account_number_required", exception.Code);
    }

    [Fact]
    public void OnlyTheAccountNumberIsBankNameRequired()
    {
        var account = WithoutBank() with { AccountNumber = "12345678" };

        var exception = Assert.Throws<QuotationsDomainException>(() => account.Normalized());

        Assert.Equal("quotation.billing.bank_name_required", exception.Code);
    }

    [Fact]
    public void AQuotationWithACompanyAndNoAccountCanBeSent()
    {
        var quotation = NewQuotation(WithoutBank());
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, Advisor, Now);

        quotation.EnsureSendable();

        Assert.True(quotation.CanBeConvertedToOrder);
    }

    [Fact]
    public void AQuotationWithoutCompanyStillNeedsIt()
    {
        var quotation = NewQuotation(null);

        var exception = Assert.Throws<QuotationsDomainException>(quotation.EnsureSendable);

        Assert.Equal("quotation.billing.account_required", exception.Code);
    }

    // La moneda pertenece a la cuenta: quitarla (dejar sólo la empresa) no la cambia, aunque la cuenta sin banco diga otra.
    [Fact]
    public void ACompanyOnlyAccountDoesNotChangeTheCurrency()
    {
        var quotation = NewQuotation(WithBank("USD"));
        Assert.Equal("USD", quotation.Currency);

        quotation.UpdateDetails(
            quotation.ValidUntil, null, null, QuotationParties.Empty, WithoutBank("COP"), repricing: null,
            isRetail: false, globalScaleFloor: null, MunicipalContext.None, Advisor, Now);

        Assert.Equal("USD", quotation.Currency);
        Assert.Null(quotation.BillingAccount!.BankName);
    }

    [Fact]
    public void AManualOrderWithoutBankAccountIsCreated()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header, QuotationParties.Empty, WithoutBank(),
            ShippingDetails.None, [Line], null, Advisor, [], Now, Today);

        Assert.NotNull(order.BillingAccount);
        Assert.Null(order.BillingAccount.BankName);
        Assert.Null(order.BillingAccount.AccountNumber);
        Assert.Equal("COP", order.Currency);
    }

    [Fact]
    public void AnExternalOrderWithoutBankAccountIsCreated()
    {
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header, QuotationParties.Empty, WithoutBank(), [Line],
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now, Today, OrderSource.Shopify, "#1001",
            ShippingDetails.None, "shop:1001", null, OrderLineCosts.None);

        Assert.Null(order.BillingAccount!.BankName);
    }

    [Fact]
    public void AStoreOrderWithoutBankAccountIsCreated()
    {
        var order = Order.CreateFromStore(
            OrderId.New(), TenantId, "PED-2026-0003", Header, QuotationParties.Empty, WithoutBank(), [Line],
            null, Advisor, Now, Today, Guid.CreateVersion7().ToString());

        Assert.Null(order.BillingAccount!.BankName);
    }
}
