using Modules.Customers.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-10 §4.5: la empresa de un cliente para Pipeline, con la etiqueta del puerto de Customers y encadenada con
/// <c>ResolveSurvivorAsync</c>.</summary>
public sealed class PipelineCustomerDirectoryCompanyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Person = Guid.CreateVersion7();

    private sealed class FakeCompanies(Dictionary<Guid, CustomerCompanyRef> byContact) : ICustomerCompanyDirectory
    {
        public Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerCompanyRef>>(byContact);

        public Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerContactRef?> FindContactOfAsync(Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static PipelineCustomerDirectory Adapter(Dictionary<Guid, CustomerCompanyRef> companies, FakeCustomerMergeStore? merges = null) =>
        new(null!, merges ?? new FakeCustomerMergeStore(), new FakeCompanies(companies));

    [Fact]
    public async Task ACustomerWithoutCompanyHasNone()
    {
        var directory = Adapter([]);

        Assert.Null(await directory.FindCompanyIdAsync(Tenant, Person, TestContext.Current.CancellationToken));
        Assert.Null(await directory.FindCompanyAsync(Tenant, Person, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheCompanyCarriesItsLabel()
    {
        var company = new CustomerCompanyRef(Guid.CreateVersion7(), "Acme SAS", true, true, null);

        var found = await Adapter(new() { [Person] = company }).FindCompanyAsync(Tenant, Person, TestContext.Current.CancellationToken);

        Assert.Equal(new Modules.Pipeline.Application.PipelineCompanyRef(company.Id, "Acme SAS"), found);
    }

    [Fact]
    public async Task AMergedCompanyResolvesToItsSurvivor()
    {
        var company = new CustomerCompanyRef(Guid.CreateVersion7(), "Acme SAS", true, true, null);
        var survivor = Guid.CreateVersion7();
        var merges = new FakeCustomerMergeStore();
        merges.SurvivorsById[company.Id] = survivor;

        var id = await Adapter(new() { [Person] = company }, merges).FindCompanyIdAsync(Tenant, Person, TestContext.Current.CancellationToken);

        Assert.Equal(survivor, id);
    }
}
