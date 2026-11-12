using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerDomainFixtures;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 contactos de empresa §3.2 y §4.7: el vínculo persona → empresa vive en el agregado.</summary>
public sealed class CustomerCompanyTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private sealed class PlusNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }

    private static Customer NewCustomer(Guid? tenantId = null) =>
        Customer.Create(
            CustomerId.New(), tenantId ?? TenantId, "CLI08000142", "Verde Esencial", null, null,
            Identification(IdentificationType.Cc, "1020304050"), null, Colombian(), Commercial(), Now);

    private static string CodeOf(Action action) => Assert.Throws<CustomersDomainException>(action).Code;

    [Fact]
    public void LinkingSetsTheCompanyAndBumpsTheVersion()
    {
        var person = NewCustomer();
        var company = NewCustomer();
        var before = person.Version;

        var changed = person.LinkToCompany(company, hasContacts: false, Later);

        Assert.True(changed);
        Assert.Equal(company.Id, person.CompanyId);
        Assert.Equal(before + 1, person.Version);
        Assert.Equal(Later, person.UpdatedAt);
    }

    [Fact]
    public void LinkingAnIncompleteCustomerKeepsItIncomplete()
    {
        var person = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Laura Pérez", "310 935 2187", "laura@verde.co", "CO", Now, new PlusNormalizer());

        person.LinkToCompany(NewCustomer(), hasContacts: false, Later);

        Assert.False(person.IsComplete);
    }

    [Fact]
    public void LinkingToTheSameCompanyAgainChangesNothing()
    {
        var person = NewCustomer();
        var company = NewCustomer();
        person.LinkToCompany(company, false, Later);
        var version = person.Version;

        var changed = person.LinkToCompany(company, false, Later.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(version, person.Version);
        Assert.Equal(Later, person.UpdatedAt);
    }

    // Ruling P10: la vuelta al mismo vínculo es un no-op aunque la empresa se haya inactivado después.
    [Fact]
    public void LinkingToTheSameCompanyAgainIsANoOpEvenIfItWasDeactivatedSince()
    {
        var person = NewCustomer();
        var company = NewCustomer();
        person.LinkToCompany(company, false, Later);
        var version = person.Version;
        company.Deactivate(Later.AddMinutes(1));

        var changed = person.LinkToCompany(company, false, Later.AddMinutes(2));

        Assert.False(changed);
        Assert.Equal(version, person.Version);
    }

    [Fact]
    public void LinkingToItselfIsRejected()
    {
        var person = NewCustomer();

        Assert.Equal("customers.customer.company_is_self", CodeOf(() => person.LinkToCompany(person, false, Later)));
        Assert.Null(person.CompanyId);
    }

    [Fact]
    public void LinkingToAMergedCompanyIsRejected()
    {
        var person = NewCustomer();
        var company = NewCustomer();
        CustomerMergePlan.Create(NewCustomer(), [company]).MarkMerged(Now);

        Assert.Equal("customers.customer.company_not_found", CodeOf(() => person.LinkToCompany(company, false, Later)));
    }

    [Fact]
    public void LinkingToACompanyOfAnotherTenantIsRejected()
    {
        var person = NewCustomer();
        var company = NewCustomer(Guid.NewGuid());

        Assert.Equal("customers.customer.company_not_found", CodeOf(() => person.LinkToCompany(company, false, Later)));
    }

    [Fact]
    public void LinkingToAnInactiveCompanyIsRejected()
    {
        var person = NewCustomer();
        var company = NewCustomer();
        company.Deactivate(Now);

        Assert.Equal("customers.customer.company_inactive", CodeOf(() => person.LinkToCompany(company, false, Later)));
    }

    [Fact]
    public void LinkingToACompanyThatIsAContactIsRejected()
    {
        var person = NewCustomer();
        var notACompany = NewCustomer();
        notACompany.LinkToCompany(NewCustomer(), false, Now);

        Assert.Equal("customers.customer.company_is_contact", CodeOf(() => person.LinkToCompany(notACompany, false, Later)));
    }

    [Fact]
    public void LinkingACustomerThatHasContactsIsRejected()
    {
        var company = NewCustomer();
        var other = NewCustomer();

        Assert.Equal("customers.customer.has_contacts", CodeOf(() => company.LinkToCompany(other, hasContacts: true, Later)));
    }

    [Fact]
    public void LinkingAMergedCustomerIsRejected()
    {
        var person = NewCustomer();
        CustomerMergePlan.Create(NewCustomer(), [person]).MarkMerged(Now);

        Assert.Equal(CustomersErrorCodes.Merged, CodeOf(() => person.LinkToCompany(NewCustomer(), false, Later)));
    }

    [Fact]
    public void UnlinkingClearsTheCompany()
    {
        var person = NewCustomer();
        person.LinkToCompany(NewCustomer(), false, Later);
        var before = person.Version;

        var changed = person.UnlinkFromCompany(Later.AddMinutes(1));

        Assert.True(changed);
        Assert.Null(person.CompanyId);
        Assert.Equal(before + 1, person.Version);
    }

    [Fact]
    public void UnlinkingWithoutCompanyChangesNothing()
    {
        var person = NewCustomer();
        var before = person.Version;

        Assert.False(person.UnlinkFromCompany(Later));
        Assert.Equal(before, person.Version);
    }

    [Fact]
    public void AMergedCustomerLosesItsCompany()
    {
        var person = NewCustomer();
        person.LinkToCompany(NewCustomer(), false, Now);

        CustomerMergePlan.Create(NewCustomer(), [person]).MarkMerged(Later);

        Assert.Null(person.CompanyId);
    }
}
