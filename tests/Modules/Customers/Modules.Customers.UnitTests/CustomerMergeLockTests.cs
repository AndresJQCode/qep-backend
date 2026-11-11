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
        var handler = new MergeCustomersHandler(
            repository, new UnitOfWork(), null!, null!, new EngagementTestBed.FakeTags(), null!, new FakeCustomerCompanyDirectory([]),
            null!, new RecordingAudit(),
            new EngagementTestBed.Context(Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerMerge),
            new FixedClock(), new MergeCustomersValidator());

        await Assert.ThrowsAnyAsync<Exception>(() => handler.HandleAsync(
            new MergeCustomersCommand(Tenant, survivor, [merged], 1), TestContext.Current.CancellationToken));

        Assert.Equal("lock-merge", repository.Log[0]);
        var call = Assert.Single(repository.MergeLockCalls);
        Assert.Equal([survivor, merged], call.Ids);
        Assert.Equal([merged], call.Companies);
    }
}
