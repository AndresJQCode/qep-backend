using BuildingBlocks.Application;
using FluentValidation;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class SupervisorTaskActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SupervisorTaskRunner Runner(TaskWorld world) =>
        new(
            world.Context(),
            world.Authorization,
            world.Repository(),
            world.UnitOfWork(),
            world.Recorder(),
            world.Clock(),
            world.Caller(),
            world.Composer());

    private static Task<TaskDto> CancelLineAsync(TaskWorld world, Guid taskId, Guid lineId, long version) =>
        new CancelTaskLineHandler(Runner(world), new CancelTaskLineValidator())
            .HandleAsync(new CancelTaskLineCommand(TaskWorld.Tenant, taskId, lineId, "sin stock", version), Ct);

    [Fact]
    public async Task AnActionLocksTheTaskBeforeLoadingItAndSavesLast()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var dto = await new HoldTaskHandler(Runner(world), new HoldTaskValidator())
            .HandleAsync(new HoldTaskCommand(TaskWorld.Tenant, task.Id, 1, "mañana"), Ct);

        Assert.Equal(("Planned", 2L), (dto.Status, dto.Version));
        Assert.Empty(dto.Events);
        Assert.Equal(["begin", "lock-task", "load-task", "save"], world.Log);
        var (action, fields) = Assert.Single(world.Audit);
        Assert.Equal("wms.task.held", action);
        Assert.Equal("mañana", fields["reason"]);
        Assert.Equal(task.Number, fields["number"]);
    }

    [Fact]
    public async Task AStaleVersionIs412AndNothingIsSaved()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            new HoldTaskHandler(Runner(world), new HoldTaskValidator())
                .HandleAsync(new HoldTaskCommand(TaskWorld.Tenant, task.Id, 7, null), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.DoesNotContain("save", world.Log);
    }

    [Fact]
    public async Task AnUnknownTaskIs404()
    {
        var world = new TaskWorld();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            CancelLineAsync(world, Guid.NewGuid(), Guid.NewGuid(), 1));

        Assert.Equal(WmsErrorCodes.TaskNotFound, error.Code);
    }

    [Fact]
    public async Task CancellingAnUnknownLineIs404BeforeTheDomain()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            CancelLineAsync(world, task.Id, Guid.NewGuid(), 1));

        Assert.Equal(WmsErrorCodes.TaskNotFound, error.Code);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public async Task CancellingTheOnlyLineCancelsTheTask()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var dto = await CancelLineAsync(world, task.Id, task.Lines[0].Id, 1);

        Assert.Equal("Cancelled", dto.Status);
        Assert.Equal("wms.task.line_cancelled", Assert.Single(world.Audit).Action);
    }

    [Fact]
    public async Task AssigningToSomeoneNotEligibleIs422()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var error = await Assert.ThrowsAsync<WmsDomainException>(() =>
            new AssignTaskHandler(Runner(world), world.Members(), new AssignTaskValidator())
                .HandleAsync(new AssignTaskCommand(TaskWorld.Tenant, task.Id, TaskWorld.OtherMember, 1, null), Ct));

        Assert.Equal(WmsErrorCodes.TaskAssigneeNotEligible, error.Code);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
    }

    [Fact]
    public async Task ASupervisorWithoutThePermissionIsDeniedBeforeValidating()
    {
        var world = new TaskWorld();
        world.Permissions.Remove(WmsPermissions.TaskManage);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new CancelTaskHandler(Runner(world), new CancelTaskValidator())
                .HandleAsync(new CancelTaskCommand(TaskWorld.Tenant, Guid.NewGuid(), "", 1), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CancellingNeedsAReason(string? reason)
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            new CancelTaskHandler(Runner(world), new CancelTaskValidator())
                .HandleAsync(
                    CancelTaskCommand.From(TaskWorld.Tenant, task.Id, new CancelTaskRequest(reason, 1)), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Reason");
    }

    [Fact]
    public void TheOtherBodiesAreValidatedByField()
    {
        var longReason = new string('r', 501);

        Assert.Contains(
            new HoldTaskValidator().Validate(new HoldTaskCommand(Guid.NewGuid(), Guid.NewGuid(), 1, longReason)).Errors,
            error => error.PropertyName == "Reason");
        Assert.Contains(
            new AssignTaskValidator().Validate(
                new AssignTaskCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1, longReason)).Errors,
            error => error.PropertyName == "MemberId");
        Assert.Contains(
            new ChangeTaskPriorityValidator().Validate(
                new ChangeTaskPriorityCommand(Guid.NewGuid(), Guid.NewGuid(), 10, false, 1)).Errors,
            error => error.PropertyName == "Priority");
        Assert.Contains(
            new ListTaskAssigneesValidator().Validate(
                new ListTaskAssigneesQuery(Guid.NewGuid(), Guid.NewGuid(), "move")).Errors,
            error => error.PropertyName == "Type");
    }

    [Fact]
    public async Task TheBoardListsEveryStatusAndTypeEvenAtZero()
    {
        var world = new TaskWorld();
        var claimed = world.AddTask();
        claimed.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start.AddMinutes(1));
        claimed.Heartbeat(TaskWorld.Member, claimed.LeaseToken!.Value, TaskWorld.Start.AddMinutes(9));
        world.AddTask();
        var handler = new GetTaskBoardHandler(
            world.Context(), world.Authorization, world.Warehouses(), world.Repository(), world.Members());

        var board = await handler.HandleAsync(new GetTaskBoardQuery(TaskWorld.Tenant, world.Warehouse.Id.Value), Ct);

        Assert.Equal(
            [("Planned", 0), ("Ready", 1), ("Assigned", 0), ("InProgress", 1), ("Done", 0), ("Cancelled", 0)],
            board.ByStatus.Select(slice => (slice.Status, slice.Count)));
        Assert.Equal([("Move", 2)], board.ByType.Select(slice => (slice.Type, slice.Open)));
        var worker = Assert.Single(board.Operators);
        Assert.Equal(("Ana Operaria", claimed.Id, claimed.Number), (worker.Name, worker.TaskId, worker.TaskNumber));
        Assert.Equal(TaskWorld.Start.AddMinutes(1), worker.Since);
        Assert.Equal(TaskWorld.Start.AddMinutes(9), worker.LastActivityAt);
    }

    [Fact]
    public async Task AssigneesComeByName()
    {
        var world = new TaskWorld();
        world.Eligible.Add(TaskWorld.OtherMember);
        world.Names[TaskWorld.OtherMember] = "Álvaro Bodega";
        var handler = new ListTaskAssigneesHandler(
            world.Context(), world.Authorization, world.Members(), new ListTaskAssigneesValidator());

        var assignees = await handler.HandleAsync(
            new ListTaskAssigneesQuery(TaskWorld.Tenant, world.Warehouse.Id.Value, "Move"), Ct);

        Assert.Equal(["Álvaro Bodega", "Ana Operaria"], assignees.Items.Select(item => item.Name));
    }
}
