using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>La selección de la siguiente tarea contra PostgreSQL real (spec 2026-11-26 §6.6 y §6.8): quién es elegible, en qué orden
/// y que dos operarios a la vez nunca reciben la misma. <see cref="TheScoreMatchesTaskPriority"/> amarra el puntaje escrito en SQL con
/// <see cref="TaskPriority"/>.</summary>
public sealed class TaskCandidateQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Al milisegundo: timestamptz guarda microsegundos y los bordes de los tramos se comparan por igualdad.
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed record Arrangement(PostgreSqlContainer Database, QepApiFactory Factory, LedgerSetup Setup, Guid Destination)
        : IAsyncDisposable
    {
        public WarehouseTask Move(
            int priority = 3,
            int releasedMinutesAgo = 0,
            TimeSpan? dueIn = null,
            Guid? assignTo = null,
            bool publish = true) =>
            NewMove(
                Setup,
                Setup.BinA,
                Destination,
                priority: priority,
                createdAt: Now.AddMinutes(-releasedMinutesAgo),
                dueAt: dueIn is { } offset ? Now + offset : null,
                assignTo: assignTo,
                publish: publish);

        public Task SaveAsync(params WarehouseTask[] tasks) => TaskTestSupport.SaveAsync(Factory, tasks);

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>Una transacción abierta que retiene lo que bloqueó hasta que se dispone, sin confirmar nada.</summary>
    private sealed class Picker : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IAsyncDisposable _transaction;

        private Picker(AsyncServiceScope scope, IAsyncDisposable transaction)
        {
            _scope = scope;
            _transaction = transaction;
        }

        public static async Task<Picker> OpenAsync(QepApiFactory factory)
        {
            var scope = factory.Services.CreateAsyncScope();
            var transaction = await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().BeginTransactionAsync(Ct);
            return new Picker(scope, transaction);
        }

        public Task<Guid?> NextAsync(
            Guid tenantId,
            Guid warehouseId,
            Guid memberId,
            IReadOnlyCollection<WarehouseTaskType>? types = null,
            CancellationToken? cancellationToken = null) =>
            _scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>().LockNextCandidateAsync(
                tenantId, warehouseId, memberId, types ?? [WarehouseTaskType.Move], Now, cancellationToken ?? Ct);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    private static async Task<Arrangement> ArrangeTasksAsync()
    {
        var (database, factory) = await OpenAsync();
        var setup = await ArrangeAsync(database, factory);
        return new Arrangement(database, factory, setup, await CreateDestinationAsync(setup));
    }

    private static async Task<Guid?> PickAsync(Arrangement arrangement, Guid? member = null)
    {
        await using var picker = await Picker.OpenAsync(arrangement.Factory);
        return await picker.NextAsync(
            arrangement.Setup.TenantId, arrangement.Setup.WarehouseId, member ?? Guid.CreateVersion7());
    }

    [Fact]
    public async Task AnAssignedTaskWinsOverAHigherPriorityPoolTask()
    {
        await using var a = await ArrangeTasksAsync();
        var member = Guid.CreateVersion7();
        var assigned = a.Move(priority: 1, assignTo: member);
        var pool = a.Move(priority: 9);
        await a.SaveAsync(pool, assigned);

        Assert.Equal(assigned.Id, await PickAsync(a, member));
        Assert.Equal(pool.Id, await PickAsync(a));
    }

    [Fact]
    public async Task ATaskAssignedToSomeoneElseIsNeverACandidate()
    {
        await using var a = await ArrangeTasksAsync();
        await a.SaveAsync(a.Move(priority: 9, assignTo: Guid.CreateVersion7()));

        Assert.Null(await PickAsync(a));
    }

    [Fact]
    public async Task UrgentBeatsAgedAndAgedIsCapped()
    {
        await using var a = await ArrangeTasksAsync();
        var urgent = a.Move(priority: 1);
        urgent.ChangePriority(1, urgent: true, Guid.CreateVersion7(), Now);
        var aged = a.Move(priority: 5, releasedMinutesAgo: 300);
        await a.SaveAsync(urgent, aged);

        // 1 + 10 contra 5 + 3.
        Assert.Equal(urgent.Id, await PickAsync(a));

        await ExecuteAsync(a.Database, "UPDATE wms.tasks SET urgent = false, priority = 8, released_at = @now WHERE id = @id",
            ("now", Now), ("id", urgent.Id));

        // 8 contra 8: sin el tope, la de 300 minutos sumaría 15 y no habría empate que resolver. Sin corte en ninguna, decide la más
        // antigua en el pool.
        Assert.Equal(aged.Id, await PickAsync(a));
        await ExecuteAsync(a.Database, "UPDATE wms.tasks SET priority = 9 WHERE id = @id", ("id", urgent.Id));
        Assert.Equal(urgent.Id, await PickAsync(a));
    }

    [Fact]
    public async Task ATieOnTheScoreGoesToTheEarliestDueDate()
    {
        await using var a = await ArrangeTasksAsync();
        // Los dos puntajes son 3: fuera de la última ventana de dos horas el corte no suma, sólo desempata, y los nulos van al final.
        var withoutDue = a.Move(releasedMinutesAgo: 10);
        var later = a.Move(releasedMinutesAgo: 5, dueIn: TimeSpan.FromHours(5));
        var sooner = a.Move(dueIn: TimeSpan.FromHours(3));
        await a.SaveAsync(withoutDue, later, sooner);

        await using var first = await Picker.OpenAsync(a.Factory);
        await using var second = await Picker.OpenAsync(a.Factory);
        await using var third = await Picker.OpenAsync(a.Factory);
        var member = Guid.CreateVersion7();

        Assert.Equal(
            [sooner.Id, later.Id, withoutDue.Id],
            [
                await first.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member),
                await second.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member),
                await third.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member),
            ]);
    }

    [Fact]
    public async Task AnOverdueTaskBeatsAFresherOneOfEqualPriority()
    {
        await using var a = await ArrangeTasksAsync();
        var fresh = a.Move(releasedMinutesAgo: 10);
        var overdue = a.Move(dueIn: TimeSpan.FromMinutes(-1));
        await a.SaveAsync(fresh, overdue);

        Assert.Equal(overdue.Id, await PickAsync(a));
    }

    [Fact]
    public async Task PlannedAndDoneTasksAreNotCandidates()
    {
        await using var a = await ArrangeTasksAsync();
        var member = Guid.CreateVersion7();
        var planned = a.Move(publish: false);
        var plannedForMe = a.Move(publish: false, assignTo: member);
        var done = a.Move();
        done.Claim(member, "device-1", Now);
        done.ConfirmLine(member, done.LeaseToken!.Value, "device-1", done.Lines[0].Id, done.Lines[0].QuantityPlanned, Now);
        var cancelled = a.Move();
        cancelled.Cancel("Ya no hace falta", Guid.CreateVersion7(), Now);
        var inProgress = a.Move();
        inProgress.Claim(Guid.CreateVersion7(), "device-2", Now);
        await a.SaveAsync(planned, plannedForMe, done, cancelled, inProgress);

        Assert.Null(await PickAsync(a, member));
    }

    [Fact]
    public async Task OnlyTheRequestedTypesWarehouseAndTenantAreCandidates()
    {
        await using var a = await ArrangeTasksAsync();
        var task = a.Move();
        await a.SaveAsync(task);
        var member = Guid.CreateVersion7();
        await using var picker = await Picker.OpenAsync(a.Factory);

        Assert.Null(await picker.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member, []));
        Assert.Null(await picker.NextAsync(a.Setup.TenantId, Guid.CreateVersion7(), member));
        Assert.Null(await picker.NextAsync(Guid.Parse(OtherTenantId), a.Setup.WarehouseId, member));
        Assert.Equal(task.Id, await picker.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member));
    }

    // DA-33: saltar la propuesta no puede devolver la misma en el toque siguiente.
    [Fact]
    public async Task ATaskISkippedIsNotOfferedToMeAgainForTheCooldown()
    {
        await using var a = await ArrangeTasksAsync();
        var member = Guid.CreateVersion7();
        var inside = Now - TaskTypePolicy.SkipCooldown + TimeSpan.FromSeconds(1);
        var skipped = a.Move(priority: 9, releasedMinutesAgo: 30);
        Release(skipped, member, TaskUnclaimReason.Skipped, inside);
        var other = a.Move(priority: 1);
        await a.SaveAsync(skipped, other);

        Assert.Equal(other.Id, await PickAsync(a, member));
        Assert.Equal(skipped.Id, await PickAsync(a));

        // Al cumplirse el plazo vuelve a ser elegible para quien la saltó.
        await ExecuteAsync(a.Database, "UPDATE wms.task_events SET occurred_at = @at WHERE task_id = @id AND kind = 'Unclaimed'",
            ("at", Now - TaskTypePolicy.SkipCooldown), ("id", skipped.Id));
        Assert.Equal(skipped.Id, await PickAsync(a, member));
    }

    [Theory]
    [InlineData(TaskUnclaimReason.ShiftEnd)]
    [InlineData(TaskUnclaimReason.CannotReach)]
    [InlineData(TaskUnclaimReason.Other)]
    public async Task OnlySkippingHidesTheTask(TaskUnclaimReason reason)
    {
        await using var a = await ArrangeTasksAsync();
        var member = Guid.CreateVersion7();
        var task = a.Move();
        Release(task, member, reason, Now);
        await a.SaveAsync(task);

        Assert.Equal(task.Id, await PickAsync(a, member));
    }

    // La asignación del supervisor manda sobre el salto (spec §6.5: lo asignado se entrega primero).
    [Fact]
    public async Task ASkippedTaskAssignedToMeIsOfferedAgain()
    {
        await using var a = await ArrangeTasksAsync();
        var member = Guid.CreateVersion7();
        var task = a.Move();
        Release(task, member, TaskUnclaimReason.Skipped, Now);
        task.Assign(member, Guid.CreateVersion7(), null, Now);
        await a.SaveAsync(task);

        Assert.Equal(task.Id, await PickAsync(a, member));
    }

    private static void Release(WarehouseTask task, Guid member, TaskUnclaimReason reason, DateTimeOffset at)
    {
        task.Claim(member, "device-1", at);
        task.Unclaim(member, task.LeaseToken!.Value, reason, at);
    }

    [Fact]
    public async Task TwoTransactionsGetDifferentTasks()
    {
        await using var a = await ArrangeTasksAsync();
        var best = a.Move(priority: 9);
        var other = a.Move(priority: 1);
        await a.SaveAsync(best, other);
        var (tenant, warehouse) = (a.Setup.TenantId, a.Setup.WarehouseId);
        // Si el segundo esperara la fila bloqueada en vez de saltarla, la prueba terminaría por este plazo.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        await using var first = await Picker.OpenAsync(a.Factory);
        await using var second = await Picker.OpenAsync(a.Factory);
        await using var third = await Picker.OpenAsync(a.Factory);

        Assert.Equal(best.Id, await first.NextAsync(tenant, warehouse, Guid.CreateVersion7(), cancellationToken: deadline.Token));
        Assert.Equal(other.Id, await second.NextAsync(tenant, warehouse, Guid.CreateVersion7(), cancellationToken: deadline.Token));
        Assert.Null(await third.NextAsync(tenant, warehouse, Guid.CreateVersion7(), cancellationToken: deadline.Token));
    }

    [Fact]
    public async Task TheScoreMatchesTaskPriority()
    {
        await using var a = await ArrangeTasksAsync();
        // Cada tramo de envejecimiento y de corte, en el borde exacto y a su lado: con un peso o un borde distinto en el SQL, alguna
        // pareja cambia de orden.
        WarehouseTask[] tasks =
        [
            a.Move(priority: 9),
            a.Move(priority: 8, releasedMinutesAgo: 19),
            a.Move(priority: 7, releasedMinutesAgo: 20),
            a.Move(priority: 6, releasedMinutesAgo: 41),
            a.Move(priority: 5, releasedMinutesAgo: 60),
            a.Move(priority: 5, releasedMinutesAgo: 400),
            a.Move(priority: 4, dueIn: TimeSpan.Zero),
            a.Move(priority: 4, dueIn: TimeSpan.FromMinutes(30)),
            a.Move(priority: 5, dueIn: TimeSpan.FromMinutes(31)),
            a.Move(priority: 5, dueIn: TimeSpan.FromHours(1)),
            a.Move(priority: 6, dueIn: TimeSpan.FromMinutes(61)),
            a.Move(priority: 6, dueIn: TimeSpan.FromHours(2)),
            a.Move(priority: 7, dueIn: TimeSpan.FromMinutes(121)),
            a.Move(priority: 1, releasedMinutesAgo: 65, dueIn: TimeSpan.FromMinutes(-90)),
            a.Move(priority: 2),
        ];
        tasks[^1].ChangePriority(2, urgent: true, Guid.CreateVersion7(), Now);
        await a.SaveAsync(tasks);

        var expected = tasks
            .OrderByDescending(task => TaskPriority.Score(task.Priority, task.Urgent, task.ReleasedAt, task.DueAt, Now))
            .ThenBy(task => task.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(task => task.ReleasedAt)
            // El orden de uuid en PostgreSQL es el de sus bytes, el mismo que el del texto en minúsculas.
            .ThenBy(task => task.Id.ToString(), StringComparer.Ordinal)
            .Select(task => task.Id)
            .ToList();

        var pickers = new List<Picker>();
        var actual = new List<Guid>();
        try
        {
            var member = Guid.CreateVersion7();
            foreach (var _ in tasks)
            {
                var picker = await Picker.OpenAsync(a.Factory);
                pickers.Add(picker);
                var id = await picker.NextAsync(a.Setup.TenantId, a.Setup.WarehouseId, member);
                Assert.NotNull(id);
                actual.Add(id.Value);
            }
        }
        finally
        {
            foreach (var picker in pickers)
            {
                await picker.DisposeAsync();
            }
        }

        Assert.Equal(expected, actual);
    }
}
