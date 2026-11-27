using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;
using Npgsql;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>La persistencia del motor de tareas contra PostgreSQL real (spec 2026-11-26 §6.1): la migración
/// <c>AddWarehouseTasks</c>, sus <c>CHECK</c>, el índice de una tarea en curso por operario y la numeración.</summary>
public sealed class WarehouseTaskPersistenceTests
{
    private static readonly TimeSpan StoragePrecision = TimeSpan.FromMilliseconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocationCodesComeOnlyFromTheTenantAndTheIndexesExist()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseRepository>();

        var mine = await warehouses.FindLocationCodesAsync(s.TenantId, [new LocationId(s.BinA)], Ct);
        var foreign = await warehouses.FindLocationCodesAsync(Guid.CreateVersion7(), [new LocationId(s.BinA)], Ct);

        Assert.Single(mine);
        Assert.Empty(foreign);
        Assert.Equal(2L, await ScalarAsync<long>(
            database,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'wms' AND indexname IN ('IX_tasks_board', 'IX_tasks_list')"));
    }

    [Fact]
    public async Task ATaskRoundTripsWithItsLinesAndEvents()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var lot = await CreateLotAsync(factory, s, s.VariantId, "L-1");
        var member = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var task = WarehouseTask.CreateMove(
            s.TenantId,
            "TSK-20261126-0001",
            new WarehouseId(s.WarehouseId),
            new OwnerId(s.OwnerId),
            7,
            now.AddHours(1),
            "Reubicar antes del corte",
            [Line(s, s.BinA, destination, 5), Line(s, s.Receiving, destination, 2.5m, lot, StockStatus.Quarantine)],
            null,
            publish: true,
            creator,
            now);
        task.Claim(member, "device-1", now);
        task.ConfirmLine(member, task.LeaseToken!.Value, "device-1", task.Lines[0].Id, 2, now);
        await SaveAsync(factory, task);

        var reloaded = await ReloadAsync(factory, task);

        Assert.Equal(WarehouseTaskStatus.InProgress, reloaded.Status);
        Assert.Equal(WarehouseTaskType.Move, reloaded.Type);
        Assert.Equal(TaskSourceType.Manual, reloaded.SourceType);
        Assert.Null(reloaded.SourceId);
        Assert.Equal(task.Number, reloaded.Number);
        Assert.Equal(task.WarehouseId, reloaded.WarehouseId);
        Assert.Equal(task.OwnerId, reloaded.OwnerId);
        Assert.Equal(7, reloaded.Priority);
        Assert.False(reloaded.Urgent);
        Assert.Equal("Reubicar antes del corte", reloaded.Note);
        Assert.Equal(member, reloaded.ClaimedByMemberId);
        Assert.Equal("device-1", reloaded.ClaimedDeviceId);
        Assert.Equal(task.LeaseToken, reloaded.LeaseToken);
        Assert.Equal(task.LeaseExpiresAt!.Value, reloaded.LeaseExpiresAt!.Value, StoragePrecision);
        Assert.Equal(task.DueAt!.Value, reloaded.DueAt!.Value, StoragePrecision);
        Assert.Equal(task.ReleasedAt!.Value, reloaded.ReleasedAt!.Value, StoragePrecision);
        Assert.Equal(creator, reloaded.CreatedBy);
        Assert.Equal(task.Version, reloaded.Version);

        Assert.Equal(
            task.Lines.Select(line => (
                line.Id, line.Position, line.VariantId, line.LotId, line.StockStatus, line.FromLocationId, line.ToLocationId,
                line.QuantityPlanned, line.QuantityConfirmed, line.Status)),
            reloaded.Lines.Select(line => (
                line.Id, line.Position, line.VariantId, line.LotId, line.StockStatus, line.FromLocationId, line.ToLocationId,
                line.QuantityPlanned, line.QuantityConfirmed, line.Status)));
        Assert.Equal(
            [TaskEventKind.Created, TaskEventKind.Published, TaskEventKind.Claimed, TaskEventKind.LineConfirmed],
            History(reloaded).Select(value => value.Kind));
        var confirmed = History(reloaded)[^1];
        Assert.Equal((s.TenantId, task.Id, member, "device-1"), (confirmed.TenantId, confirmed.TaskId, confirmed.MemberId, confirmed.DeviceId));
        Assert.Equal((task.Lines[0].Id, 2m), (confirmed.LineId, confirmed.Quantity));
    }

    [Fact]
    public async Task AShortLineRoundTrips()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var member = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var task = NewMove(s, s.BinA, destination, createdAt: now.AddMinutes(-1));
        task.Claim(member, "device-1", now);
        task.ShortLine(member, task.LeaseToken!.Value, "device-1", task.Lines[0].Id, 2, now);
        await SaveAsync(factory, task);

        var reloaded = await ReloadAsync(factory, task);

        Assert.Equal(WarehouseTaskStatus.Done, reloaded.Status);
        var line = Assert.Single(reloaded.Lines);
        Assert.Equal((TaskLineStatus.Short, 5m, 2m), (line.Status, line.QuantityPlanned, line.QuantityConfirmed));
        var shortEvent = Assert.Single(History(reloaded), value => value.Kind == TaskEventKind.LineShort);
        Assert.Equal((task.Lines[0].Id, 3m), (shortEvent.LineId, shortEvent.Quantity));
        Assert.Equal("Short", await ScalarAsync<string>(database, "SELECT line_status FROM wms.task_lines"));
    }

    // El camino de un handler: la tarea se carga sin su historia, cambia, y sólo se insertan los hechos nuevos.
    [Fact]
    public async Task AChangeOnALoadedTaskAppendsItsEventAndBumpsTheVersion()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var task = NewMove(s, s.BinA, destination);
        await SaveAsync(factory, task);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>().GetAsync(s.TenantId, task.Id, Ct);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.Events);
            Assert.Single(loaded.Lines);
            loaded.Hold(Guid.CreateVersion7(), "Falta espacio", DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        var reloaded = await ReloadAsync(factory, task);
        Assert.Equal(WarehouseTaskStatus.Planned, reloaded.Status);
        Assert.Null(reloaded.ReleasedAt);
        Assert.Equal(2, reloaded.Version);
        Assert.Equal(
            [TaskEventKind.Created, TaskEventKind.Published, TaskEventKind.Held],
            History(reloaded).Select(value => value.Kind));
    }

    [Fact]
    public async Task AnotherTenantNeverSeesTheTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = NewMove(s, s.BinA, await CreateDestinationAsync(s));
        await SaveAsync(factory, task);
        var stranger = Guid.Parse(OtherTenantId);

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();

        Assert.Null(await repository.GetAsync(stranger, task.Id, Ct));
        Assert.Null(await repository.GetWithEventsAsync(stranger, task.Id, Ct));
        Assert.Equal(0, (await repository.ListAsync(stranger, null, null, null, null, 1, 50, Ct)).TotalCount);
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        Assert.False(await repository.LockAsync(stranger, task.Id, Ct));
        Assert.True(await repository.LockAsync(s.TenantId, task.Id, Ct));
    }

    [Fact]
    public async Task TheListFiltersAndTheBoardQueriesReadTheSameRows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var member = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var pool = NewMove(s, s.BinA, destination, createdAt: now.AddMinutes(-3));
        var assigned = NewMove(s, s.BinA, destination, assignTo: member, createdAt: now.AddMinutes(-2));
        var taken = NewMove(s, s.BinA, destination, createdAt: now.AddMinutes(-1));
        taken.Claim(member, "device-1", now);
        await SaveAsync(factory, pool, assigned, taken);

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();

        var page = await repository.ListAsync(s.TenantId, s.WarehouseId, null, WarehouseTaskType.Move, null, 1, 2, Ct);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal([taken.Id, assigned.Id], page.Items.Select(value => value.Id));
        Assert.All(page.Items, value => Assert.Single(value.Lines));
        var filtered = await repository.ListAsync(s.TenantId, null, WarehouseTaskStatus.Assigned, null, member, 1, 50, Ct);
        Assert.Equal(assigned.Id, Assert.Single(filtered.Items).Id);

        var counts = await repository.CountByStatusAsync(s.TenantId, s.WarehouseId, Ct);
        Assert.Equal(
            [(WarehouseTaskStatus.Ready, 1), (WarehouseTaskStatus.Assigned, 1), (WarehouseTaskStatus.InProgress, 1)],
            counts.OrderBy(value => value.Status).Select(value => (value.Status, value.Count)));
        Assert.Equal(taken.Id, Assert.Single(await repository.ListInProgressAsync(s.TenantId, s.WarehouseId, Ct)).Id);
        Assert.Equal(assigned.Id, Assert.Single(await repository.ListAssignedToAsync(s.TenantId, s.WarehouseId, member, Ct)).Id);
        Assert.Equal(taken.Id, (await repository.FindInProgressOfMemberAsync(s.TenantId, member, Ct))?.Id);
        Assert.Null(await repository.FindInProgressOfMemberAsync(s.TenantId, Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task TheDatabaseRejectsAnInProgressTaskWithoutLease()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = NewMove(s, s.BinA, await CreateDestinationAsync(s));
        await SaveAsync(factory, task);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "UPDATE wms.tasks SET status = 'InProgress' WHERE id = @id", ("id", task.Id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_tasks_claim", exception.ConstraintName);
    }

    [Fact]
    public async Task TwoInProgressTasksOfTheSameMemberCollideOnTheIndex()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var member = Guid.CreateVersion7();
        var first = NewMove(s, s.BinA, destination);
        first.Claim(member, "device-1", DateTimeOffset.UtcNow);
        var second = NewMove(s, s.BinA, destination);
        second.Claim(member, "device-2", DateTimeOffset.UtcNow);
        await SaveAsync(factory, first);

        var exception = await Assert.ThrowsAsync<TaskMemberBusyException>(() => SaveAsync(factory, second));

        Assert.Equal(WmsErrorCodes.TaskMemberBusy, exception.Code);
        Assert.Empty(exception.ProblemExtensions);
    }

    [Fact]
    public async Task DeletingALocationUsedByATaskIs422HasStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await SaveAsync(factory, NewMove(s, s.BinA, destination));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{WmsUrl()}/warehouses/{s.WarehouseId}/locations/{destination}");

        var response = await s.Manager.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.has_stock", await CodeOfAsync(response));
    }

    [Fact]
    public async Task NumbersAreSequentialPerTenantAndDay()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var prefix = "TSK-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";

        async Task<string> NumberAndSaveAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
            var number = await repository.NextNumberAsync(s.TenantId, today, Ct);
            repository.Add(WarehouseTask.CreateMove(
                s.TenantId, number, new WarehouseId(s.WarehouseId), new OwnerId(s.OwnerId), null, null, null,
                [Line(s, s.BinA, destination)], null, true, Guid.CreateVersion7(), DateTimeOffset.UtcNow));
            await unitOfWork.SaveChangesAsync(Ct);
            return number;
        }

        Assert.Equal(prefix + "0001", await NumberAndSaveAsync());
        Assert.Equal(prefix + "0002", await NumberAndSaveAsync());
        await using var other = factory.Services.CreateAsyncScope();
        var unit = other.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        await using var open = await unit.BeginTransactionAsync(Ct);
        Assert.Equal(
            prefix + "0001",
            await other.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>()
                .NextNumberAsync(Guid.Parse(OtherTenantId), today, Ct));
    }
}
