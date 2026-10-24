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

        public List<CustomerPhoneMatch>? ByPhone { get; init; }

        public (string E164, int Take)? LastListByPhone { get; private set; }

        public Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(Guid tenantId, string e164, int take, CancellationToken cancellationToken)
        {
            LastListByPhone = (e164, take);
            return ByPhone is null ? throw new NotSupportedException() : Task.FromResult<IReadOnlyList<CustomerPhoneMatch>>(ByPhone);
        }
    }

    private sealed class FakeWhatsApp : ICustomerWhatsAppDirectory
    {
        public CustomerContact? Contact { get; init; }

        public DateTimeOffset? AudienceCreatedAt { get; init; }

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
            yield return new CustomerContact(Guid.NewGuid(), "Uno", "+573001112233", null, true, false, AudienceCreatedAt);
            yield return new CustomerContact(Guid.NewGuid(), "Dos", null, "CO.2", false, true, AudienceCreatedAt);
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

        public List<Guid> OptedOutByButton { get; } = [];

        public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
        {
            OptedOutByButton.Add(customerId);
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

    private sealed class FakeTagLookup : ICustomerTagLookup
    {
        public List<CustomerTagRef> Tags { get; } = [];

        public Task<IReadOnlyList<CustomerTagRef>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerTagRef>>(Tags.Where(tag => tagIds.Contains(tag.Id)).ToList());

        public Task<IReadOnlyList<Guid>> FindCustomerIdsAsync(Guid tenantId, Guid tagId, int cap, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerTagRef>>> FindForCustomersAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerTagRef>>>(new Dictionary<Guid, IReadOnlyList<CustomerTagRef>>());
    }

    /// <summary>Spec 2026-10-14 decisión 10: la rama de Instagram no entra en estas pruebas.</summary>
    private sealed class FakeInstagram : ICustomerInstagramDirectory
    {
        public Task<EnsuredCustomer> EnsureAsync(Guid tenantId, InstagramContact contact, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static MessagingCustomerDirectory AdapterOver(FakeWhatsApp whatsApp, FakeTagLookup? tags = null, FakePhones? phones = null) =>
        new(phones ?? new FakePhones(), whatsApp, new FakeInstagram(), tags ?? new FakeTagLookup(), NullLogger<MessagingCustomerDirectory>.Instance);

    [Fact]
    public async Task FindTagsMapsToTheMessagingRecordAndSkipsWhatDoesNotExist()
    {
        var tags = new FakeTagLookup();
        var live = Guid.NewGuid();
        tags.Tags.Add(new CustomerTagRef(live, "Mayoristas", "blue"));

        var found = await AdapterOver(new FakeWhatsApp(), tags).FindTagsAsync(Tenant, [live, Guid.NewGuid()], TestContext.Current.CancellationToken);

        Assert.Equal([new MessagingTagRef(live, "Mayoristas", "blue")], found);
    }

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
    public async Task TheAudiencePassesTheCreationDateThrough()
    {
        // Spec 2026-10-24 §4.4: sin conversación gana el cliente más viejo del número, y la fecha la tiene Customers.
        var createdAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var whatsApp = new FakeWhatsApp { AudienceCreatedAt = createdAt };
        var seen = new List<MessagingCustomerContact>();

        await foreach (var contact in AdapterOver(whatsApp).ListAudienceAsync(Tenant, [], [Guid.NewGuid()], TestContext.Current.CancellationToken))
        {
            seen.Add(contact);
        }

        Assert.All(seen, contact => Assert.Equal(createdAt, contact.CreatedAt));
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

    [Fact]
    public async Task OptOutByButtonPassesThroughToItsOwnPort()
    {
        var whatsApp = new FakeWhatsApp();
        var customer = Guid.NewGuid();

        await AdapterOver(whatsApp).MarkMarketingOptOutByButtonAsync(Tenant, customer, TestContext.Current.CancellationToken);

        Assert.Equal([customer], whatsApp.OptedOutByButton);
        Assert.Empty(whatsApp.OptedOut);
    }

    [Fact]
    public async Task ListByPhoneAddsThePlusAndMapsToCustomerRefs()
    {
        var first = new CustomerPhoneMatch(Guid.NewGuid(), "Laura", true);
        var second = new CustomerPhoneMatch(Guid.NewGuid(), "Pedro", false);
        var phones = new FakePhones { ByPhone = [first, second] };

        var refs = await AdapterOver(new FakeWhatsApp(), phones: phones).ListByPhoneAsync(Tenant, "573001112233", 11, TestContext.Current.CancellationToken);

        Assert.Equal(("+573001112233", 11), phones.LastListByPhone);
        Assert.Equal([new CustomerRefDto(first.Id, "Laura", true), new CustomerRefDto(second.Id, "Pedro", false)], refs);
    }
}
