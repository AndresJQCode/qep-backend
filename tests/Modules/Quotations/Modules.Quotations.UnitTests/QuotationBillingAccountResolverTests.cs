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
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.currency.unsupported", exception.Code);
    }

    // Spec 2026-11-16 withholdings by company: only the company was chosen, so the preview gets a pending account with the
    // company's country and city and the tenant default currency, and no bank data.
    [Fact]
    public async Task ACompanyWithoutAccountResolvesAPendingAccountOfThatCompany()
    {
        var company = Company(Guid.NewGuid(), "CO", Guid.NewGuid());
        var lookup = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef> { [company.Id] = company });

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            lookup, Guid.NewGuid(), null, company.Id, "COP", TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Equal(
            (company.Id, "", "", "COP", (string?)"CO", company.CityId, true),
            (account.CompanyId, account.BankName, account.AccountNumber, account.Currency, account.Country, account.CityId,
                account.IsPending));
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
        Assert.Equal((accountCompany.Id, "12345678", false), (account.CompanyId, account.AccountNumber, account.IsPending));
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
