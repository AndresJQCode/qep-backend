using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Infrastructure.Workers;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>El barrido de leases vencidos contra el esquema real (spec 2026-11-26 §6.7). El temporizador no corre en las pruebas (el
/// harness fija una hora de espera de arranque): cada prueba llama <c>RunOnceAsync</c> con su propio <c>now</c>.</summary>
public sealed class TaskLeaseSweeperTests
{
    private static readonly DateTimeOffset Claimed = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskLeaseSweeper SweeperOf(QepApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();

    private static WarehouseTask ClaimedMove(LedgerSetup s, Guid destination, Guid? assignTo = null, DateTimeOffset? claimedAt = null)
    {
        var at = claimedAt ?? Claimed;
        var task = NewMove(s, s.BinA, destination, assignTo: assignTo, createdAt: at);
        task.Claim(assignTo ?? Guid.CreateVersion7(), "device-1", at);
        return task;
    }

    [Fact]
    public async Task AnExpiredClaimGoesBackToThePool()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = ClaimedMove(s, await CreateDestinationAsync(s));
        await SaveAsync(factory, task);

        var expired = await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct);

        Assert.Equal(1, expired);
        var reloaded = await ReloadAsync(factory, task);
        Assert.Equal(WarehouseTaskStatus.Ready, reloaded.Status);
        Assert.Equal(task.Version + 1, reloaded.Version);
        // El reclamo se conserva: es lo que deja reanudar una confirmación tardía.
        Assert.Equal(task.ClaimedByMemberId, reloaded.ClaimedByMemberId);
        Assert.Equal(task.LeaseToken, reloaded.LeaseToken);
        Assert.Null(reloaded.LeaseExpiresAt);
        var last = History(reloaded)[^1];
        Assert.Equal(TaskEventKind.LeaseExpired, last.Kind);
        Assert.Null(last.MemberId);
    }

    [Fact]
    public async Task AnExpiredAssignedClaimGoesBackToItsAssignee()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var member = Guid.CreateVersion7();
        var task = ClaimedMove(s, await CreateDestinationAsync(s), member);
        await SaveAsync(factory, task);

        Assert.Equal(1, await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct));

        var reloaded = await ReloadAsync(factory, task);
        Assert.Equal(WarehouseTaskStatus.Assigned, reloaded.Status);
        Assert.Equal(member, reloaded.AssignedMemberId);
    }

    [Fact]
    public async Task ALiveLeaseIsUntouched()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = ClaimedMove(s, await CreateDestinationAsync(s));
        await SaveAsync(factory, task);

        Assert.Equal(0, await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(5), Ct));

        var reloaded = await ReloadAsync(factory, task);
        Assert.Equal(WarehouseTaskStatus.InProgress, reloaded.Status);
        Assert.Equal(task.Version, reloaded.Version);
        Assert.DoesNotContain(reloaded.Events, value => value.Kind == TaskEventKind.LeaseExpired);
    }

    // Una confirmación en curso tiene la fila bloqueada: el barrido la salta en vez de esperarla.
    [Fact]
    public async Task ATaskLockedByAnotherTransactionIsSkipped()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = ClaimedMove(s, await CreateDestinationAsync(s));
        await SaveAsync(factory, task);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>().LockAsync(s.TenantId, task.Id, Ct));

            Assert.Equal(0, await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), deadline.Token));
        }

        Assert.Equal(1, await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct));
    }

    // Más que un lote: la pasada sigue hasta que no queda ninguna.
    [Fact]
    public async Task APassSweepsMoreThanOneBatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await SaveAsync(factory, [.. Enumerable.Range(0, 101).Select(index => ClaimedMove(s, destination))]);

        Assert.Equal(101, await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks WHERE status = 'InProgress'"));
    }

    [Fact]
    public async Task TwoSweepsAtOnceNeverExpireTheSameTaskTwice()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await SaveAsync(factory, ClaimedMove(s, destination), ClaimedMove(s, destination), ClaimedMove(s, destination));
        var sweeper = SweeperOf(factory);

        var results = await Task.WhenAll(
            Task.Run(() => sweeper.RunOnceAsync(Claimed.AddMinutes(16), Ct), Ct),
            Task.Run(() => sweeper.RunOnceAsync(Claimed.AddMinutes(16), Ct), Ct));

        Assert.Equal(3, results.Sum());
        Assert.Equal(3L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE kind = 'LeaseExpired'"));
        Assert.Equal(
            3L, await ScalarAsync<long>(database, "SELECT count(DISTINCT task_id) FROM wms.task_events WHERE kind = 'LeaseExpired'"));
    }
}
