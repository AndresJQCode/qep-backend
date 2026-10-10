using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-10 §8.2 y §9.4 sin base: quien pierde la carrera del índice único relee y devuelve al
/// ganador, tanto al crear como al vincular por teléfono; un término de búsqueda en blanco no consulta nada.</summary>
public sealed class CustomerWhatsAppDirectoryRaceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class EchoNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }

    private sealed class NoAudit : ICustomersAuditPublisher
    {
        public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
        {
        }
    }

    private sealed class TakenUnitOfWork : ICustomersUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => throw new WhatsAppUserIdTakenException();
    }

    private sealed class FakeRepository : ICustomerRepository
    {
        public Queue<Customer?> ByWhatsAppUserId { get; } = new();

        public Customer? ByPhone { get; init; }

        public bool AttachIsTaken { get; init; }

        public bool AttachResult { get; init; } = true;

        public bool ReplaceIsTaken { get; init; }

        public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
            Task.FromResult(ByWhatsAppUserId.Count == 0 ? null : ByWhatsAppUserId.Dequeue());

        public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
            Task.FromResult(ByPhone);

        public Task<bool> TryAttachWhatsAppUserIdAsync(Guid tenantId, CustomerId customerId, string whatsAppUserId, CancellationToken cancellationToken) =>
            AttachIsTaken ? throw new WhatsAppUserIdTakenException() : Task.FromResult(AttachResult);

        public Task<bool> TryReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
            ReplaceIsTaken ? throw new WhatsAppUserIdTakenException() : Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A blank term must not reach the repository.");

        public void Add(Customer customer)
        {
        }

        public Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds, bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> ListForExportAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyWithClassificationAsync(Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(Guid tenantId, IReadOnlyCollection<string> suffixes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, string>> FindNamesByIdsAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, Customer>> FindManyAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Customer Incomplete(string whatsAppUserId) =>
        Customer.CreateIncomplete(CustomerId.New(), Tenant, "Laura", "+573001234567", "CO", whatsAppUserId, Now, new EchoNormalizer());

    private static CustomerWhatsAppDirectory DirectoryOver(ICustomerRepository repository) =>
        new(repository, new TakenUnitOfWork(), new NoAudit(), new EchoNormalizer(), new FixedClock());

    [Fact]
    public async Task LosingTheCreateRaceReturnsTheWinnerAsExisting()
    {
        var winner = Incomplete("CO.RACE");
        var repository = new FakeRepository();
        repository.ByWhatsAppUserId.Enqueue(null);
        repository.ByWhatsAppUserId.Enqueue(winner);

        var result = await DirectoryOver(repository).EnsureAsync(
            Tenant, new WhatsAppContact("CO.RACE", null, "Laura", null), TestContext.Current.CancellationToken);

        Assert.Equal(new EnsuredCustomer(winner.Id.Value, EnsureOutcome.Existing), result);
    }

    [Fact]
    public async Task LosingTheAttachRaceReturnsTheWinnerAsExisting()
    {
        var winner = Incomplete("CO.RACE");
        var repository = new FakeRepository { ByPhone = Incomplete("CO.OTHER"), AttachIsTaken = true };
        repository.ByWhatsAppUserId.Enqueue(null);
        repository.ByWhatsAppUserId.Enqueue(winner);

        var result = await DirectoryOver(repository).EnsureAsync(
            Tenant, new WhatsAppContact("CO.RACE", "+573001234567", "Laura", null), TestContext.Current.CancellationToken);

        Assert.Equal(new EnsuredCustomer(winner.Id.Value, EnsureOutcome.Existing), result);
    }

    // D-A6: el UPDATE condicional no encontró la columna vacía; el cliente sigue siendo el del teléfono.
    [Fact]
    public async Task AnAttachThatFindsAnotherBsuidStillLinks()
    {
        var byPhone = Incomplete("CO.FIRST");
        var repository = new FakeRepository { ByPhone = byPhone, AttachResult = false };

        var result = await DirectoryOver(repository).EnsureAsync(
            Tenant, new WhatsAppContact("CO.SECOND", "+573001234567", "Laura", null), TestContext.Current.CancellationToken);

        Assert.Equal(new EnsuredCustomer(byPhone.Id.Value, EnsureOutcome.Linked), result);
    }

    [Fact]
    public async Task AReplaceThatLosesTheIndexReturnsFalse() =>
        Assert.False(await DirectoryOver(new FakeRepository { ReplaceIsTaken = true }).ReplaceWhatsAppUserIdAsync(
            Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankTermFindsNoIds(string term) =>
        Assert.Empty(await DirectoryOver(new FakeRepository()).FindIdsByNameAsync(Tenant, term, 200, TestContext.Current.CancellationToken));
}
