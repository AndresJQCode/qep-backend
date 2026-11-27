using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Infrastructure.Workers;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Un pod más nuevo puede escribir valores que este build no conoce (un tipo, un estado de línea, un origen del corte
/// siguiente) y los lee el barrido y el tablero de uno más viejo durante el despliegue. Una fila así no puede detener el barrido (una
/// fila que lanza deshace todo el lote) ni romper el tablero (spec 2026-11-26 §13, decisión 18 del plan). Cada prueba tiene su base,
/// así que puede quitar un <c>CHECK</c> para escribir el valor «del futuro».</summary>
public sealed class TaskForwardCompatibilityTests
{
    private static readonly DateTimeOffset Claimed = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskLeaseSweeper SweeperOf(QepApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();

    private static WarehouseTask ClaimedMove(LedgerSetup s, Guid destination)
    {
        var task = NewMove(s, s.BinA, destination, createdAt: Claimed);
        task.Claim(Guid.CreateVersion7(), "device-1", Claimed);
        return task;
    }

    [Fact]
    public async Task TheSweeperSkipsATaskOfATypeThisBuildDoesNotKnow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var known = ClaimedMove(s, destination);
        var future = ClaimedMove(s, destination);
        await SaveAsync(factory, known, future);
        await ExecuteAsync(database, "ALTER TABLE wms.tasks DROP CONSTRAINT \"CK_tasks_type\"");
        await ExecuteAsync(database, "UPDATE wms.tasks SET type = 'FutureType' WHERE id = @id", ("id", future.Id));

        var expired = await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct);

        Assert.Equal(1, expired);
        Assert.Equal(WarehouseTaskStatus.Ready, (await ReloadAsync(factory, known)).Status);
        Assert.Equal("InProgress", await ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", future.Id)));
    }

    [Fact]
    public async Task TheSweeperSkipsATaskWithALineStatusThisBuildDoesNotKnow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var known = ClaimedMove(s, destination);
        var future = ClaimedMove(s, destination);
        await SaveAsync(factory, known, future);
        await ExecuteAsync(database, "ALTER TABLE wms.task_lines DROP CONSTRAINT \"CK_task_lines_status\"");
        await ExecuteAsync(database, "UPDATE wms.task_lines SET line_status = 'FutureStatus' WHERE task_id = @id", ("id", future.Id));

        var expired = await SweeperOf(factory).RunOnceAsync(Claimed.AddMinutes(16), Ct);

        Assert.Equal(1, expired);
        Assert.Equal(WarehouseTaskStatus.Ready, (await ReloadAsync(factory, known)).Status);
        Assert.Equal("InProgress", await ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", future.Id)));
    }

    [Fact]
    public async Task TheBoardIgnoresTypesAndStatusesThisBuildDoesNotKnow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var futureType = ClaimedMove(s, destination);
        var futureStatus = NewMove(s, s.BinA, destination, createdAt: Claimed);
        var ready = NewMove(s, s.BinA, destination, createdAt: Claimed);
        var assignedFuture = NewMove(s, s.BinA, destination, assignTo: Guid.CreateVersion7(), createdAt: Claimed);
        await SaveAsync(factory, futureType, futureStatus, ready, assignedFuture);
        await ExecuteAsync(database, "ALTER TABLE wms.tasks DROP CONSTRAINT \"CK_tasks_type\"");
        await ExecuteAsync(database, "ALTER TABLE wms.tasks DROP CONSTRAINT \"CK_tasks_status\"");
        await ExecuteAsync(database, "UPDATE wms.tasks SET type = 'FutureType' WHERE id = @id", ("id", futureType.Id));
        await ExecuteAsync(database, "UPDATE wms.tasks SET status = 'FutureStatus' WHERE id = @id", ("id", futureStatus.Id));
        await ExecuteAsync(database, "UPDATE wms.tasks SET type = 'FutureType' WHERE id = @id", ("id", assignedFuture.Id));

        var response = await s.Manager.GetAsync($"{TasksUrl()}/summary?warehouseId={s.WarehouseId}", Ct);

        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        var board = (await response.Content.ReadFromJsonAsync<TaskBoardDto>(Ct))!;
        // Sólo los nombres que este build conoce, y sólo las filas que puede leer: la Ready de tipo Move.
        Assert.Equal(["Planned", "Ready", "Assigned", "InProgress", "Done", "Cancelled"], board.ByStatus.Select(slice => slice.Status));
        Assert.Equal(1, board.ByStatus.Single(slice => slice.Status == "Ready").Count);
        Assert.Equal(0, board.ByStatus.Single(slice => slice.Status == "InProgress").Count);
        Assert.Equal(["Move", "Count"], board.ByType.Select(slice => slice.Type));
        Assert.Equal(1, board.ByType.Single(slice => slice.Type == "Move").Open);
        Assert.Empty(board.Operators);
        Assert.Empty(board.StaleAssignments);
    }
}
