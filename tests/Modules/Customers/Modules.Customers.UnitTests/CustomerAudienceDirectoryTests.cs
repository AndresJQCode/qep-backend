using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.4: lo que <see cref="ICustomerWhatsAppDirectory"/> le ofrece a Messaging para campañas
/// y envíos con plantilla: contacto, audiencia por lotes de 500, conteo y marca de opt-out.</summary>
public sealed class CustomerAudienceDirectoryTests
{
    private sealed class FakeAudience(int total) : ICustomerAudienceRepository
    {
        public List<(Guid? After, int Take)> Calls { get; } = [];

        public List<(Guid Customer, MarketingConsentSource Source, DateTimeOffset At)> OptOuts { get; } = [];

        private readonly Guid[] _ids = Enumerable.Range(1, total)
            .Select(index => new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]))
            .ToArray();

        public Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult<CustomerContact?>(
                customerId == _ids.FirstOrDefault() ? new CustomerContact(customerId, "Laura", "+573001234567", null, true, false) : null);

        public Task<IReadOnlyList<CustomerContact>> ListAudienceBatchAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, Guid? afterId, int take, CancellationToken cancellationToken)
        {
            Calls.Add((afterId, take));
            var batch = _ids.Where(id => afterId is null || id.CompareTo(afterId.Value) > 0).OrderBy(id => id).Take(take)
                .Select(id => new CustomerContact(id, "Cliente", "+573001234567", null, true, false))
                .ToArray();
            return Task.FromResult<IReadOnlyList<CustomerContact>>(batch);
        }

        public Task<AudienceCount> CountAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult(new AudienceCount(10, 1, 2, 3));

        public Task MarkMarketingOptOutAsync(
            Guid tenantId, Guid customerId, MarketingConsentSource source, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            OptOuts.Add((customerId, source, occurredAt));
            return Task.CompletedTask;
        }
    }

    private static CustomerWhatsAppDirectory DirectoryOver(ICustomerAudienceRepository audience) =>
        new(new FakeCustomers(), new CountingUnitOfWork(), new RecordingAudit(), new FakeNormalizer(), new FixedClock(), audience);

    private static async Task<List<CustomerContact>> DrainAsync(IAsyncEnumerable<CustomerContact> source)
    {
        var list = new List<CustomerContact>();
        await foreach (var contact in source)
        {
            list.Add(contact);
        }

        return list;
    }

    [Fact]
    public async Task TheAudienceIsReadInBatchesOfFiveHundredKeyedById()
    {
        var audience = new FakeAudience(1200);

        var all = await DrainAsync(DirectoryOver(audience).ListAudienceAsync(TenantId, [], [], TestContext.Current.CancellationToken));

        Assert.Equal(1200, all.Count);
        Assert.Equal(1200, all.Select(contact => contact.CustomerId).Distinct().Count());
        Assert.Equal([500, 500, 500], audience.Calls.Select(call => call.Take));
        Assert.Null(audience.Calls[0].After);
        Assert.Equal(all[499].CustomerId, audience.Calls[1].After);
        Assert.Equal(all[999].CustomerId, audience.Calls[2].After);
    }

    [Fact]
    public async Task AnExactMultipleOfTheBatchSizeEndsWithOneMoreEmptyRead()
    {
        var audience = new FakeAudience(500);

        var all = await DrainAsync(DirectoryOver(audience).ListAudienceAsync(TenantId, [], [], TestContext.Current.CancellationToken));

        Assert.Equal(500, all.Count);
        Assert.Equal(2, audience.Calls.Count);
    }

    [Fact]
    public async Task AnEmptyAudienceYieldsNothingAfterOneRead()
    {
        var audience = new FakeAudience(0);

        var all = await DrainAsync(DirectoryOver(audience).ListAudienceAsync(TenantId, [], [], TestContext.Current.CancellationToken));

        Assert.Empty(all);
        Assert.Single(audience.Calls);
    }

    [Fact]
    public async Task FindContactAndCountPassThrough()
    {
        var audience = new FakeAudience(1);
        var directory = DirectoryOver(audience);
        var id = (await audience.ListAudienceBatchAsync(TenantId, [], [], null, 1, TestContext.Current.CancellationToken)).Single().CustomerId;

        Assert.Equal("Laura", (await directory.FindContactAsync(TenantId, id, TestContext.Current.CancellationToken))?.Name);
        Assert.Null(await directory.FindContactAsync(TenantId, Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal(new AudienceCount(10, 1, 2, 3), await directory.CountAudienceAsync(TenantId, [], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MarkingTheOptOutRecordsMetaAsTheSourceWithTheClockTime()
    {
        var audience = new FakeAudience(1);
        var customer = Guid.NewGuid();

        await DirectoryOver(audience).MarkMarketingOptOutAsync(TenantId, customer, TestContext.Current.CancellationToken);

        Assert.Equal((customer, MarketingConsentSource.Meta, Now), Assert.Single(audience.OptOuts));
    }

    [Fact]
    public async Task MarkingTheOptOutByButtonRecordsButtonAsTheSourceWithTheClockTime()
    {
        var audience = new FakeAudience(1);
        var customer = Guid.NewGuid();

        await DirectoryOver(audience).MarkMarketingOptOutByButtonAsync(TenantId, customer, TestContext.Current.CancellationToken);

        Assert.Equal((customer, MarketingConsentSource.Button, Now), Assert.Single(audience.OptOuts));
    }
}
