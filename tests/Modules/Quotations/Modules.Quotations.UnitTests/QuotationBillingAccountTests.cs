using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// A pending account (spec 2026-11-16 withholdings by company, «Decided while planning») is the company without the bank
/// account yet, built only inside a preview: its bank fields are empty on purpose, so normalising it must not ask for them.
/// A real account keeps every rule it had.
/// </summary>
public sealed class QuotationBillingAccountTests
{
    [Fact]
    public void NormalizedWhenPendingAcceptsEmptyBankFields()
    {
        var companyId = Guid.NewGuid();
        var cityId = Guid.NewGuid();
        var pending = new QuotationBillingAccount
        {
            CompanyId = companyId,
            BankName = "",
            AccountNumber = "",
            Currency = "cop",
            Country = "co",
            CityId = cityId,
            IsPending = true,
        };

        var normalized = pending.Normalized();

        Assert.Equal(
            (companyId, "", "", "COP", "CO", (Guid?)cityId, true),
            (normalized.CompanyId, normalized.BankName, normalized.AccountNumber, normalized.Currency, normalized.Country,
                normalized.CityId, normalized.IsPending));
    }

    [Fact]
    public void NormalizedWhenNotPendingStillRequiresAccountNumber()
    {
        var account = new QuotationBillingAccount
        {
            CompanyId = Guid.NewGuid(),
            BankName = "Bancolombia",
            AccountNumber = "",
            Currency = "COP",
        };

        var exception = Assert.Throws<QuotationsDomainException>(() => account.Normalized());

        Assert.Equal("quotation.billing.account_number_required", exception.Code);
    }
}
