using Microsoft.Extensions.Logging.Abstractions;
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-11 §4.4: el adaptador que le da a Messaging el contacto, la audiencia, el conteo y la marca de opt-out de
/// Customers, con tipos propios de Messaging (que no referencia Customers).
/// </summary>
public sealed class MessagingCustomerDirectoryAudienceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private sealed class FakePhones : ICustomerPhoneDirectory
    {
        public Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
            Guid tenantId, IReadOnlyCollection<string> phonesE164, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWhatsApp : ICustomerWhatsAppDirectory
    {
        public CustomerContact? Contact { get; init; }

        public List<Guid> OptedOut { get; } = [];

        public (IReadOnlyCollection<Guid> Tags, IReadOnlyCollection<Guid> Customers)? LastAudience { get; private set; }

        public Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Contact);

        public async IAsyncEnumerable<CustomerContact> ListAudienceAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> tagIds,
            IReadOnlyCollection<Guid> customerIds,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastAudience = (tagIds, customerIds);
            yield return new CustomerContact(Guid.NewGuid(), "Uno", "+573001112233", null, true, false);
            yield return new CustomerContact(Guid.NewGuid(), "Dos", null, "CO.2", false, true);
            await Task.CompletedTask;
        }

        public Task<AudienceCount> CountAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult(new AudienceCount(10, 1, 2, 3));

        public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
        {
            OptedOut.Add(customerId);
            return Task.CompletedTask;
        }

        public Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static MessagingCustomerDirectory AdapterOver(FakeWhatsApp whatsApp) =>
        new(new FakePhones(), whatsApp, NullLogger<MessagingCustomerDirectory>.Instance);

    [Fact]
    public async Task FindContactMapsToTheMessagingRecordOrNull()
    {
        var id = Guid.NewGuid();
        var found = await AdapterOver(new FakeWhatsApp { Contact = new CustomerContact(id, "Laura", "+573001234567", "CO.1", true, true) })
            .FindContactAsync(Tenant, id, TestContext.Current.CancellationToken);
        var missing = await AdapterOver(new FakeWhatsApp()).FindContactAsync(Tenant, id, TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingCustomerContact(id, "Laura", "+573001234567", "CO.1", true, true), found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task TheAudienceStreamsMessagingRecordsAndPassesTheFiltersThrough()
    {
        var whatsApp = new FakeWhatsApp();
        var tags = new[] { Guid.NewGuid() };
        var customers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var seen = new List<MessagingCustomerContact>();

        await foreach (var contact in AdapterOver(whatsApp).ListAudienceAsync(Tenant, tags, customers, TestContext.Current.CancellationToken))
        {
            seen.Add(contact);
        }

        Assert.Equal(["Uno", "Dos"], seen.Select(contact => contact.Name));
        Assert.False(seen[1].IsActive);
        Assert.True(seen[1].MarketingOptOut);
        Assert.Equal("CO.2", seen[1].WhatsAppUserId);
        Assert.Equal(tags, whatsApp.LastAudience!.Value.Tags);
        Assert.Equal(customers, whatsApp.LastAudience!.Value.Customers);
    }

    [Fact]
    public async Task CountAndOptOutPassThrough()
    {
        var whatsApp = new FakeWhatsApp();
        var adapter = AdapterOver(whatsApp);
        var customer = Guid.NewGuid();

        var count = await adapter.CountAudienceAsync(Tenant, [], [], TestContext.Current.CancellationToken);
        await adapter.MarkMarketingOptOutAsync(Tenant, customer, TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingAudienceCount(10, 1, 2, 3), count);
        Assert.Equal([customer], whatsApp.OptedOut);
    }
}
