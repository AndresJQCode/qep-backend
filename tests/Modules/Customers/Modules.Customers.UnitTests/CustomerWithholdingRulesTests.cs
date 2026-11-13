using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

public sealed class CustomerWithholdingRulesTests
{
    private static CustomerTaxRateRef Rate(
        string type, string country = "CO", bool active = true, bool withholds = true, string? name = null) =>
        new(Guid.NewGuid(), name ?? $"{type} {country}", type, country, 2.5m, active, withholds);

    private static Task Validate(ICustomerTaxRateLookup lookup, params Guid[] ids) =>
        CustomerWithholdingRules.ValidateAsync(lookup, Tenant, ids, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EmptySelectionNeedsNoLookup() =>
        await Validate(new ThrowingLookup());

    [Fact]
    public async Task OneRatePerTypeIsAccepted()
    {
        var retefuente = Rate("Retefuente");
        var reteIca = Rate("ReteICA");

        await Validate(new TaxRates(retefuente, reteIca), retefuente.Id, reteIca.Id);
    }

    [Fact]
    public async Task TheSameTypeInAnotherCountryIsAccepted()
    {
        var colombia = Rate("Retefuente");
        var spain = Rate("Retefuente", country: "ES");

        await Validate(new TaxRates(colombia, spain), colombia.Id, spain.Id);
    }

    [Fact]
    public async Task ADuplicatedIdCountsOnce()
    {
        var retefuente = Rate("Retefuente");

        await Validate(new TaxRates(retefuente), retefuente.Id, retefuente.Id);
    }

    [Fact]
    public async Task TwoRatesOfTheSameTypeAndCountryAreRepeated()
    {
        var first = Rate("Retefuente");
        var second = Rate("Retefuente");

        var error = await Assert.ThrowsAsync<CustomersDomainException>(
            () => Validate(new TaxRates(first, second), first.Id, second.Id));

        Assert.Equal("customers.customer.withholding_type_repeated", error.Code);
    }

    [Fact]
    public async Task AnUnknownIdIsInvalid()
    {
        var error = await Assert.ThrowsAsync<CustomersDomainException>(
            () => Validate(new TaxRates(), Guid.NewGuid()));

        Assert.Equal("customers.customer.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public async Task AnInactiveRateIsInvalid()
    {
        var inactive = Rate("Retefuente", active: false);

        var error = await Assert.ThrowsAsync<CustomersDomainException>(
            () => Validate(new TaxRates(inactive), inactive.Id));

        Assert.Equal("customers.customer.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public async Task ARateThatDoesNotWithholdIsInvalid()
    {
        var vat = Rate("IVA", withholds: false);

        var error = await Assert.ThrowsAsync<CustomersDomainException>(
            () => Validate(new TaxRates(vat), vat.Id));

        Assert.Equal("customers.customer.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public async Task AnInvalidRateWinsOverARepeatedType()
    {
        var first = Rate("Retefuente");
        var second = Rate("Retefuente");

        var error = await Assert.ThrowsAsync<CustomersDomainException>(
            () => Validate(new TaxRates(first, second), first.Id, second.Id, Guid.NewGuid()));

        Assert.Equal("customers.customer.withholding_tax_rate_invalid", error.Code);
    }

    private sealed class ThrowingLookup : ICustomerTaxRateLookup
    {
        public Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An empty selection must not query the catalog.");

        public Task<IReadOnlyList<CustomerTaxRateRef>> ListWithholdingAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected.");
    }
}
