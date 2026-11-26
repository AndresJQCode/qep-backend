using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationBillingAccountResolverTests
{
    // Spec D6: one code for any currency outside the catalogue. Checked before the company lookup:
    // no company can make an unknown currency quotable.
    [Fact]
    public async Task AnAccountInACurrencyOutsideTheCatalogueIsRejected()
    {
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>());

        var exception = await Assert.ThrowsAsync<TenantDomainException>(() =>
            QuotationBillingAccountResolver.ResolveAsync(
                lookup,
                Guid.NewGuid(),
                new QuotationBillingAccountRequest(Guid.NewGuid(), "Bancolombia", "12345678", "MXN"),
                "COP",
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.currency.unsupported", exception.Code);
    }

    // Spec 2026-11-17 (R3): only the company was chosen, so the account carries the company's country, city and VAT, the
    // currency the caller decides, and no bank data.
    [Fact]
    public async Task ACompanyWithoutAccountResolvesTheCompanyWithNullBankFields()
    {
        var company = Company(Guid.NewGuid(), "CO", Guid.NewGuid()) with { ChargesVat = false };
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef> { [company.Id] = company });

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            lookup, Guid.NewGuid(), null, company.Id, "COP", TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Equal(
            (company.Id, (string?)null, (string?)null, "COP", (string?)"CO", company.CityId, false, false),
            (account.CompanyId, account.BankName, account.AccountNumber, account.Currency, account.Country, account.CityId,
                account.HasBankAccount, account.ChargesVat));
    }

    // Sin banco ni número, la moneda la decide quien llama; la del request se ignora (R3).
    [Fact]
    public async Task ARequestWithoutBankIgnoresItsCurrency()
    {
        var company = Company(Guid.NewGuid(), "CO", null);
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef> { [company.Id] = company });

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            lookup,
            Guid.NewGuid(),
            new QuotationBillingAccountRequest(company.Id, null, null, "USD"),
            "COP",
            TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Equal(("COP", false), (account.Currency, account.HasBankAccount));
    }

    [Fact]
    public async Task AnUnknownCompanyWithoutAccountIsCompanyNotFound()
    {
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>());

        var exception = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            QuotationBillingAccountResolver.ResolveAsync(
                lookup, Guid.NewGuid(), null, Guid.NewGuid(), "COP", TestContext.Current.CancellationToken));

        Assert.Equal(QuotationBillingAccountVerifier.CompanyNotFoundCode, exception.Code);
    }

    // The account already names its company: the company id of the body is ignored.
    [Fact]
    public async Task AnAccountWinsOverTheCompanyId()
    {
        var accountCompany = Company(Guid.NewGuid(), "CO", null);
        var other = Company(Guid.NewGuid(), "ES", null);
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>
        {
            [accountCompany.Id] = accountCompany,
            [other.Id] = other,
        });

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            lookup,
            Guid.NewGuid(),
            new QuotationBillingAccountRequest(accountCompany.Id, "Bancolombia", "12345678", "COP"),
            other.Id,
            "COP",
            TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Equal(
            (accountCompany.Id, (string?)"12345678", true),
            (account.CompanyId, account.AccountNumber, account.HasBankAccount));
    }

    [Fact]
    public async Task NeitherAccountNorCompanyIsNoAccount()
    {
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>());

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            lookup, Guid.NewGuid(), null, null, "COP", TestContext.Current.CancellationToken);

        Assert.Null(account);
    }

    private static QuotationCompanyRef Company(Guid id, string country, Guid? cityId) =>
        new(id, "Verde S.A.S.", "900123456", "NIT", country, true, null, null,
            [new QuotationCompanyAccountRef("Bancolombia", "12345678", "COP")], cityId);
}
