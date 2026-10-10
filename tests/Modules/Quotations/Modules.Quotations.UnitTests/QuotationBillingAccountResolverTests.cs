using Modules.Quotations.Application;
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
}
