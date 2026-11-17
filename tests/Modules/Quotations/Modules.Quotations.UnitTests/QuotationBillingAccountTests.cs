using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Una empresa sin cuenta (spec 2026-11-17, R3) es banco y número nulos: normalizarla no los pide y conserva lo demás. Una
/// cuenta con banco sigue con todas sus reglas.
/// </summary>
public sealed class QuotationBillingAccountTests
{
    [Fact]
    public void NormalizedWithoutBankKeepsTheCompanyData()
    {
        var companyId = Guid.NewGuid();
        var cityId = Guid.NewGuid();
        var withoutBank = new QuotationBillingAccount
        {
            CompanyId = companyId,
            BankName = "",
            AccountNumber = "",
            Currency = "cop",
            Country = "co",
            CityId = cityId,
            ChargesVat = false,
        };

        var normalized = withoutBank.Normalized();

        Assert.Equal(
            (companyId, (string?)null, (string?)null, "COP", (string?)"CO", (Guid?)cityId, false),
            (normalized.CompanyId, normalized.BankName, normalized.AccountNumber, normalized.Currency, normalized.Country,
                normalized.CityId, normalized.ChargesVat));
    }

    [Fact]
    public void NormalizedWithABankStillRequiresAccountNumber()
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
