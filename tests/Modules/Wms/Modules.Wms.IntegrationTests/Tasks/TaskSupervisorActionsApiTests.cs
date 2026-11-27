using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Las acciones del supervisor, el tablero y los asignables (spec 2026-11-26 §6.10) contra el esquema real.</summary>
public sealed class TaskSupervisorActionsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LedgerSetup Setup, Guid Destination, TaskDto Task)> ArrangeTaskAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, QepApiFactory factory)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        return (s, destination, task);
    }

    [Fact]
    public async Task HoldThenPublishRoundTrips()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);

        var held = await ActionAsync(s.Manager, task.Id, "hold", new { version = task.Version, reason = "mañana" });
        var published = await ActionAsync(s.Manager, task.Id, "publish", new { version = held.Version });

        Assert.Equal(("Planned", task.Version + 1), (held.Status, held.Version));
        Assert.Null(held.ReleasedAt);
        Assert.Equal(("Ready", task.Version + 2), (published.Status, published.Version));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.held", task.Id.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.published", task.Id.ToString()));
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);

        var response = await ActionRawAsync(s.Manager, task.Id, "hold", new { version = task.Version + 1 });

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("Ready", (await GetAsync(s.Manager, task.Id)).Status);
    }

    [Fact]
    public async Task AssignAndUnassign()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);
        var (worker, client) = await SeedOperatorAsync(factory);
        var (advisor, advisorClient) = await SeedOperatorAsync(factory, SecondOperatorSubject, ["advisor"]);
        using var _w = client;
        using var _a = advisorClient;

        var rejected = await ActionRawAsync(
            s.Manager, task.Id, "assign", new { memberId = advisor, version = task.Version });
        var assigned = await ActionAsync(s.Manager, task.Id, "assign", new { memberId = worker, version = task.Version });
        var freed = await ActionAsync(s.Manager, task.Id, "unassign", new { version = assigned.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("wms.task.assignee_not_eligible", await CodeOfAsync(rejected));
        Assert.Equal(("Assigned", worker), (assigned.Status, assigned.AssignedMemberId));
        Assert.Equal("Ready", freed.Status);
        Assert.Null(freed.AssignedMemberId);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.assigned", task.Id.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.unassigned", task.Id.ToString()));
    }

    [Fact]
    public async Task ChangingPriorityShowsInEffectivePriority()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);

        var changed = await ActionAsync(
            s.Manager, task.Id, "priority", new { priority = 7, urgent = true, version = task.Version });

        Assert.Equal((7, true, 17), (changed.Priority, changed.Urgent, changed.EffectivePriority));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.priority_changed", task.Id.ToString()));
    }

    [Fact]
    public async Task CancellingClosesOpenLinesAndCancellingTwiceIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);

        var cancelled = await ActionAsync(s.Manager, task.Id, "cancel", new { reason = "pedido anulado", version = task.Version });
        var again = await ActionRawAsync(s.Manager, task.Id, "cancel", new { reason = "otra vez", version = cancelled.Version });

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Cancelled", Assert.Single(cancelled.Lines).LineStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        using var problem = JsonDocument.Parse(await BodyAsync(again));
        Assert.Equal("wms.task.invalid_transition", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Cancelled", problem.RootElement.GetProperty("from").GetString());
        Assert.Equal("cancel", problem.RootElement.GetProperty("action").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.cancelled", task.Id.ToString()));
    }

    [Fact]
    public async Task CancellingTheOnlyLineCancelsTheTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (s, _, task) = await ArrangeTaskAsync(database, factory);
        var line = Assert.Single(task.Lines).Id;

        var unknown = await ActionRawAsync(
            s.Manager, task.Id, $"lines/{Guid.CreateVersion7()}/cancel", new { reason = "no", version = task.Version });
        var cancelled = await ActionAsync(
            s.Manager, task.Id, $"lines/{line}/cancel", new { reason = "sin stock", version = task.Version });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("wms.task.not_found", await CodeOfAsync(unknown));
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.line_cancelled", task.Id.ToString()));
    }

    [Fact]
    public async Task TheBoardListsEveryStatusEvenAtZeroAndWhoIsWorking()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var (worker, client) = await SeedOperatorAsync(factory);
        using var _w = client;
        var claimed = NewMove(s, s.BinA, destination);
        claimed.Claim(worker, "zebra-01", DateTimeOffset.UtcNow);
        await SaveAsync(factory, claimed, NewMove(s, s.BinA, destination));

        var board = await s.Manager.GetFromJsonAsync<TaskBoardDto>(
            $"{TasksUrl()}/summary?warehouseId={s.WarehouseId}", Ct);

        Assert.NotNull(board);
        Assert.Equal(6, board.ByStatus.Count);
        Assert.Equal(1, board.ByStatus.Single(slice => slice.Status == "InProgress").Count);
        Assert.Equal(0, board.ByStatus.Single(slice => slice.Status == "Done").Count);
        Assert.Equal(2, Assert.Single(board.ByType).Open);
        var working = Assert.Single(board.Operators);
        Assert.Equal((worker, claimed.Id), (working.MemberId, working.TaskId));
        Assert.NotNull(working.Since);
    }

    [Fact]
    public async Task TheBoardListsAssignmentsTheAssigneeCanNoLongerPerform()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var (worker, client) = await SeedOperatorAsync(factory);
        var (advisor, advisorClient) = await SeedOperatorAsync(factory, SecondOperatorSubject, ["advisor"]);
        using var _w = client;
        using var _a = advisorClient;
        // Sembradas por el dominio: la API ya no deja asignar a quien no tiene el permiso, que es justo el caso de perderlo después.
        var orphan = NewMove(s, s.BinA, destination, assignTo: advisor);
        await SaveAsync(factory, orphan, NewMove(s, s.BinA, destination, assignTo: worker));

        var board = await s.Manager.GetFromJsonAsync<TaskBoardDto>($"{TasksUrl()}/summary?warehouseId={s.WarehouseId}", Ct);

        Assert.NotNull(board);
        var stale = Assert.Single(board.StaleAssignments);
        Assert.Equal((orphan.Id, orphan.Number, advisor, "Caller"), (stale.TaskId, stale.TaskNumber, stale.MemberId, stale.Name));
    }

    [Fact]
    public async Task AssigneesOnlyListMembersWithThePermission()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (worker, client) = await SeedOperatorAsync(factory);
        var (advisor, advisorClient) = await SeedOperatorAsync(factory, SecondOperatorSubject, ["advisor"]);
        using var _w = client;
        using var _a = advisorClient;

        var assignees = await s.Manager.GetFromJsonAsync<TaskAssigneesDto>(
            $"{TasksUrl()}/assignees?warehouseId={s.WarehouseId}&type=Move", Ct);

        Assert.NotNull(assignees);
        Assert.Contains(assignees.Items, item => item.MemberId == worker);
        Assert.DoesNotContain(assignees.Items, item => item.MemberId == advisor);
    }

    [Fact]
    public async Task ActionsWithoutTaskManageAre403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (_, _, task) = await ArrangeTaskAsync(database, factory);
        using var reader = CreateReader(factory);

        foreach (var action in new[] { "publish", "hold", "assign", "unassign", "priority", "cancel" })
        {
            var response = await ActionRawAsync(reader, task.Id, action, new { version = task.Version });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
