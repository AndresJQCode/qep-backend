using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 §4.7: la fusión toma, antes de leer a nadie, el mismo conjunto de filas que el vínculo más los
/// contactos que va a mover, en una sola sentencia ordenada.</summary>
public sealed class CustomerMergeLockTests
{
    [Fact]
    public async Task TheMergeLocksSurvivorMergedAndTheirContactsBeforeReadingAnyone()
    {
        var repository = new FakeCustomerRepository();
        var survivor = Guid.NewGuid();
        var merged = Guid.NewGuid();
        var chained = Guid.NewGuid();
        var references = new LoggingReferences(repository.Log);
        var handler = new MergeCustomersHandler(
            repository, new UnitOfWork(), new ChainStore([chained]), references, new EngagementTestBed.FakeTags(), null!,
            new FakeCustomerCompanyDirectory([]),
            null!, new RecordingAudit(),
            new EngagementTestBed.Context(Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerMerge),
            new FixedClock(), new MergeCustomersValidator());

        await Assert.ThrowsAnyAsync<Exception>(() => handler.HandleAsync(
            new MergeCustomersCommand(Tenant, survivor, [merged], 1), TestContext.Current.CancellationToken));

        // Merge de develop con borrar clientes: primero los candados de referencias (survivor, fusionados y cadena), después las
        // filas FOR UPDATE (survivor, fusionados y sus contactos). Siempre en ese orden, antes de leer a nadie.
        Assert.Equal("lock-references", repository.Log[0]);
        Assert.Equal("lock-merge", repository.Log[1]);
        Assert.Equal(new[] { survivor, merged, chained }.Order(), references.Ids.Order());
        var call = Assert.Single(repository.MergeLockCalls);
        Assert.Equal([survivor, merged], call.Ids);
        Assert.Equal([merged], call.Companies);
    }

    private sealed class LoggingReferences(List<string> log) : ICustomerMergeReferences
    {
        public List<Guid> Ids { get; } = [];

        public Task LockAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
        {
            log.Add("lock-references");
            Ids.AddRange(customerIds);
            return Task.CompletedTask;
        }

        public Task MoveAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerMergeReferenceCounts> CountAsync(Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ChainStore(Guid[] chain) : ICustomerMergeStore
    {
        public Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(chain);

        public Task RepointMergedIntoAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountContactsMovedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RepointContactsAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerMergeOwnedCounts> CountOwnedAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MoveOwnedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindSurvivorIdByIdentificationAsync(Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
