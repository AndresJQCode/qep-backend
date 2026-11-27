using BuildingBlocks.Application;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class TaskClosedNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SpyHook(TaskWorld world) : ITaskClosedHook
    {
        public List<TaskClosed> Closed { get; } = [];

        public Task OnClosedAsync(TaskClosed closed, CancellationToken cancellationToken)
        {
            world.Log.Add("hook");
            Closed.Add(closed);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingHook : ITaskClosedHook
    {
        public Task OnClosedAsync(TaskClosed closed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hook failed");
    }

    private static Task<TaskDto> ConfirmAsync(TaskWorld world, ConfirmTaskLineCommand command) =>
        new ConfirmTaskLineHandler(
                world.LineRunner("key-1"), world.Warehouses(), world.Ledger(), world.Clock(), new ConfirmTaskLineValidator())
            .HandleAsync(command, Ct);

    private static (WarehouseTask Task, ConfirmTaskLineCommand Command) Claimed(TaskWorld world, decimal quantity)
    {
        var task = world.AddTask(lines: world.Line(5));
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start);
        var command = new ConfirmTaskLineCommand(
            TaskWorld.Tenant, task.Id, task.Lines[0].Id, task.LeaseToken!.Value, world.From.Value, world.To.Value, null,
            quantity, null, world.Now, TaskWorld.Device);
        return (task, command);
    }

    [Fact]
    public async Task ConfirmingTheLastQuantityNotifiesTheHookOnceBeforeTheReceipt()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var (_, command) = Claimed(world, 5);

        await ConfirmAsync(world, command);

        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "lock-task", "load-task", "ledger", "hook", "receipt", "save"],
            world.Log);
        var closed = Assert.Single(spy.Closed);
        Assert.Equal(TaskWorld.Member, closed.ActorMemberId);
        Assert.Equal("key-1", closed.OperationKey);
        Assert.Equal(command.OccurredAt, closed.OccurredAt);
    }

    [Fact]
    public async Task APartialConfirmationDoesNotNotify()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var (_, command) = Claimed(world, 2);

        await ConfirmAsync(world, command);

        Assert.DoesNotContain("hook", world.Log);
        Assert.Empty(spy.Closed);
    }

    [Fact]
    public async Task CancellingTheTaskNotifiesOnce()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var task = world.AddTask();

        await new CancelTaskHandler(world.SupervisorRunner(), new CancelTaskValidator())
            .HandleAsync(new CancelTaskCommand(TaskWorld.Tenant, task.Id, "sin stock", 1), Ct);

        var closed = Assert.Single(spy.Closed);
        Assert.Null(closed.OperationKey);
        Assert.Equal(TaskWorld.Member, closed.ActorMemberId);
        Assert.Equal(WarehouseTaskStatus.Cancelled, closed.Task.Status);
    }

    [Fact]
    public async Task CancellingTheLastOpenLineNotifiesOnce()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var task = world.AddTask();

        await new CancelTaskLineHandler(world.SupervisorRunner(), new CancelTaskLineValidator())
            .HandleAsync(new CancelTaskLineCommand(TaskWorld.Tenant, task.Id, task.Lines[0].Id, "sin stock", 1), Ct);

        var closed = Assert.Single(spy.Closed);
        Assert.Null(closed.OperationKey);
        Assert.Equal(TaskWorld.Member, closed.ActorMemberId);
    }

    [Fact]
    public async Task CancellingALineThatLeavesOthersOpenDoesNotNotify()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var task = world.AddTask(lines: [world.Line(5), world.Line(3)]);

        await new CancelTaskLineHandler(world.SupervisorRunner(), new CancelTaskLineValidator())
            .HandleAsync(new CancelTaskLineCommand(TaskWorld.Tenant, task.Id, task.Lines[0].Id, "sin stock", 1), Ct);

        Assert.Empty(spy.Closed);
    }

    [Fact]
    public async Task AFailingHookSavesNothing()
    {
        var world = new TaskWorld();
        world.Hooks.Add(new FailingHook());
        var (_, command) = Claimed(world, 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(world, command));

        Assert.DoesNotContain("receipt", world.Log);
        Assert.DoesNotContain("save", world.Log);
    }

    [Fact]
    public async Task ALostLeaseCommitsOnlyTheConflictAndNeverNotifies()
    {
        var world = new TaskWorld();
        var spy = new SpyHook(world);
        world.Hooks.Add(spy);
        var (_, command) = Claimed(world, 5);

        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(
            () => ConfirmAsync(world, command with { LeaseToken = Guid.NewGuid() }));

        Assert.False(error.Resumable);
        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "lock-task", "load-task", "save"], world.Log);
        Assert.Empty(spy.Closed);
        Assert.Empty(world.Posted);
        Assert.Empty(world.Receipts);
        var conflict = Assert.Single(world.Incidents);
        Assert.Equal((IncidentType.SyncConflict, "key-1", TaskWorld.Member), (conflict.Type, conflict.OperationKey, conflict.ReportedBy));
    }

    // Sólo quien tuvo el reclamo deja novedad: si no, cambiando la Idempotency-Key se inundaría la bandeja.
    [Fact]
    public async Task ACallerWhoNeverClaimedTheTaskLeavesNoConflict()
    {
        var world = new TaskWorld();
        var (_, command) = Claimed(world, 5);
        world.CallerMember = TaskWorld.OtherMember;

        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(
            () => ConfirmAsync(world, command with { LeaseToken = Guid.NewGuid() }));

        Assert.False(error.Resumable);
        Assert.Empty(world.Incidents);
        Assert.DoesNotContain("save", world.Log);
    }
}
