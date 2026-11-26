using Microsoft.Extensions.Logging.Abstractions;
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-10 §4: el cliente de la conversación lleva su empresa, resuelta en una sola consulta por página.</summary>
public sealed class MessagingCustomerDirectoryCompanyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private sealed class FakePhones(Dictionary<string, CustomerPhoneMatch> matches) : ICustomerPhoneDirectory
    {
        public Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
            Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, CustomerPhoneMatch>>(matches);

        public Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchActiveAsync(
            Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(Guid tenantId, string e164, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWhatsApp(Dictionary<Guid, CustomerWhatsAppRef> refs) : ICustomerWhatsAppDirectory
    {
        public Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>>(refs);

        public Task<EnsuredCustomer> EnsureByPhoneAsync(Guid tenantId, WhatsAppPhoneContact contact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<CustomerContact> ListAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AudienceCount> CountAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingCompanies(Dictionary<Guid, CustomerCompanyRef> byContact) : ICustomerCompanyDirectory
    {
        public int Calls { get; private set; }

        public List<Guid> Asked { get; } = [];

        public Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
        {
            Calls++;
            Asked.AddRange(customerIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, CustomerCompanyRef>>(byContact);
        }

        public Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerContactRef?> FindContactOfAsync(Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static MessagingCustomerDirectory Adapter(
        CountingCompanies companies, Dictionary<string, CustomerPhoneMatch>? matches = null, Dictionary<Guid, CustomerWhatsAppRef>? refs = null) =>
        new(new FakePhones(matches ?? []), null!, new FakeWhatsApp(refs ?? []), null!, null!, null!, companies, NullLogger<MessagingCustomerDirectory>.Instance);

    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Beto = Guid.CreateVersion7();
    private static readonly CustomerCompanyRef Acme = new(Guid.CreateVersion7(), "Acme SAS", true, false, "900123");

    [Fact]
    public async Task FindRefsAddsTheCompanyOfEachContact()
    {
        var companies = new CountingCompanies(new() { [Ana] = Acme });
        var refs = new Dictionary<Guid, CustomerWhatsAppRef> { [Ana] = new(Ana, "Ana", true), [Beto] = new(Beto, "Beto", false) };

        var found = await Adapter(companies, refs: refs).FindRefsAsync(Tenant, [Ana, Beto], TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerCompanyRefDto(Acme.Id, "Acme SAS", true, false), found[Ana].Company);
        Assert.Equal("Ana", found[Ana].Name);
    }

    [Fact]
    public async Task MatchAddsTheCompanyOfEachContact()
    {
        var companies = new CountingCompanies(new() { [Ana] = Acme });
        var matches = new Dictionary<string, CustomerPhoneMatch> { ["+573001112233"] = new(Ana, "Ana", true) };

        var found = await Adapter(companies, matches).MatchAsync(Tenant, ["573001112233"], TestContext.Current.CancellationToken);

        Assert.Equal(Acme.Id, found["573001112233"].Company!.Id);
        Assert.Equal(1, companies.Calls);
    }

    [Fact]
    public async Task ACustomerWithoutCompanyHasCompanyNull()
    {
        var companies = new CountingCompanies([]);
        var refs = new Dictionary<Guid, CustomerWhatsAppRef> { [Beto] = new(Beto, "Beto", false) };

        var found = await Adapter(companies, refs: refs).FindRefsAsync(Tenant, [Beto], TestContext.Current.CancellationToken);

        Assert.Null(found[Beto].Company);
        Assert.Equal(new CustomerRefDto(Beto, "Beto", false), found[Beto]);
    }

    [Fact]
    public async Task OneDirectoryCallServesTheWholePage()
    {
        var companies = new CountingCompanies(new() { [Ana] = Acme });
        var refs = new Dictionary<Guid, CustomerWhatsAppRef> { [Ana] = new(Ana, "Ana", true), [Beto] = new(Beto, "Beto", false) };

        await Adapter(companies, refs: refs).FindRefsAsync(Tenant, [Ana, Beto], TestContext.Current.CancellationToken);

        Assert.Equal(1, companies.Calls);
        Assert.Equivalent(new[] { Ana, Beto }, companies.Asked);
    }

    [Fact]
    public async Task AnEmptyPageDoesNotCallTheDirectory()
    {
        var companies = new CountingCompanies([]);

        await Adapter(companies).FindRefsAsync(Tenant, [], TestContext.Current.CancellationToken);

        Assert.Equal(0, companies.Calls);
    }
}
