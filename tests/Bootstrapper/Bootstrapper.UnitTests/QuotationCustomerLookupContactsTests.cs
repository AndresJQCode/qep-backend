using Modules.Customers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-10 §4.6: los contactos de la empresa de un documento llegan a Quotations por el puerto de Customers, que ya
/// descarta lo fusionado, lo de otro tenant y lo de otra empresa. El adaptador sólo traduce, acota a 100 y no inventa.</summary>
public sealed class QuotationCustomerLookupContactsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Company = Guid.CreateVersion7();

    private sealed class FakeCompanies(params CustomerContactRef[] contacts) : ICustomerCompanyDirectory
    {
        public int? LastTake { get; private set; }

        public (Guid Tenant, Guid Company, Guid Contact)? LastFind { get; private set; }

        public Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(
            Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken)
        {
            LastTake = take;
            return Task.FromResult<IReadOnlyList<CustomerContactRef>>(companyId == Company ? [.. contacts] : []);
        }

        public Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerContactRef?> FindContactOfAsync(
            Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken)
        {
            LastFind = (tenantId, companyId, contactId);
            return Task.FromResult(contacts.FirstOrDefault(contact => companyId == Company && contact.Id == contactId));
        }
    }

    private static QuotationCustomerLookup Adapter(FakeCompanies companies) => new(null!, null!, null!, companies);

    [Fact]
    public async Task AContactOfTheCompanyIsFoundWithNameAndPhone()
    {
        var laura = new CustomerContactRef(Guid.CreateVersion7(), "Laura Gómez", "3115550101", "laura@acme.co", true, true);
        var companies = new FakeCompanies(laura);

        var found = await Adapter(companies).FindContactAsync(Tenant, Company, laura.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new QuotationClientContact(laura.Id, "Laura Gómez", "3115550101"), found);
        Assert.Equal((Tenant, Company, laura.Id), companies.LastFind);
    }

    [Fact]
    public async Task ASomeoneThePortDoesNotReturnIsNotAContact()
    {
        var laura = new CustomerContactRef(Guid.CreateVersion7(), "Laura Gómez", null, null, true, true);
        var adapter = Adapter(new FakeCompanies(laura));

        Assert.Null(await adapter.FindContactAsync(Tenant, Guid.CreateVersion7(), laura.Id, TestContext.Current.CancellationToken));
        Assert.Null(await adapter.FindContactAsync(Tenant, Company, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheListIsCappedAtOneHundredAndKeepsPhoneNull()
    {
        var laura = new CustomerContactRef(Guid.CreateVersion7(), "Laura Gómez", null, null, true, true);
        var companies = new FakeCompanies(laura);

        var contacts = await Adapter(companies).ListContactsAsync(Tenant, Company, TestContext.Current.CancellationToken);

        Assert.Equal(new QuotationClientContact(laura.Id, "Laura Gómez", null), Assert.Single(contacts));
        Assert.Equal(100, companies.LastTake);
    }
}
