using BuildingBlocks.Application;
using FluentValidation;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class OperatorTaskHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OperatorTaskGuard Guard(TaskWorld world) => new(world.Context(), world.Authorization);

    private static Task<NextTaskResultDto> NextAsync(
        TaskWorld world, Guid? warehouse = null, string device = TaskWorld.Device) =>
        new NextTaskHandler(
                world.Context(),
                world.Authorization,
                world.Caller(),
                world.UnitOfWork(),
                world.Repository(),
                world.Clock(),
                world.Composer(),
                new NextTaskValidator())
            .HandleAsync(
                new NextTaskCommand(TaskWorld.Tenant, warehouse ?? world.Warehouse.Id.Value, device), Ct);

    private static Task<TaskDto> ClaimAsync(TaskWorld world, Guid taskId, string device = TaskWorld.Device) =>
        new ClaimTaskHandler(
                world.Context(),
                world.Authorization,
                world.Caller(),
                world.UnitOfWork(),
                world.Repository(),
                Guard(world),
                world.Clock(),
                world.Composer(),
                new ClaimTaskValidator())
            .HandleAsync(new ClaimTaskCommand(TaskWorld.Tenant, taskId, device), Ct);

    private static Task<TaskDto> ConfirmAsync(TaskWorld world, ConfirmTaskLineCommand command, string? key = "key-1") =>
        new ConfirmTaskLineHandler(
                world.LineRunner(key), world.Warehouses(), world.Ledger(), world.Clock(), new ConfirmTaskLineValidator())
            .HandleAsync(command, Ct);

    private static (WarehouseTask Task, ConfirmTaskLineCommand Command) ClaimedByCaller(
        TaskWorld world, decimal quantity = 5, LotId? lot = null)
    {
        var task = world.AddTask(lines: world.Line(5, lot));
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start);
        return (task, Command(world, task, quantity));
    }

    private static ConfirmTaskLineCommand Command(TaskWorld world, WarehouseTask task, decimal quantity = 5) =>
        new(
            TaskWorld.Tenant,
            task.Id,
            task.Lines[0].Id,
            task.LeaseToken!.Value,
            world.From.Value,
            world.To.Value,
            task.Lines[0].LotId?.Value,
            quantity,
            null,
            world.Now,
            TaskWorld.Device);

    private static async Task AssertDomainAsync(TaskWorld world, ConfirmTaskLineCommand command, string code)
    {
        var error = await Assert.ThrowsAsync<WmsDomainException>(() => ConfirmAsync(world, command));

        Assert.Equal(code, error.Code);
        Assert.Empty(world.Posted);
        Assert.DoesNotContain("save", world.Log);
    }

    [Fact]
    public async Task ConfirmingLocksTheMemberThenTheTaskBeforeTheLedger()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        var dto = await ConfirmAsync(world, command);

        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "lock-task", "load-task", "ledger", "receipt", "save"],
            world.Log);
        Assert.Equal("Done", dto.Status);
        Assert.Empty(dto.Events);
        Assert.Same(dto, Assert.Single(world.Receipts));
    }

    [Fact]
    public async Task TheMovementGoesFromTheLineSourceToItsDestinationWithTheTaskAsDocument()
    {
        var world = new TaskWorld();
        var (task, command) = ClaimedByCaller(world, quantity: 2);
        world.Now = TaskWorld.Start.AddMinutes(3);

        var dto = await ConfirmAsync(world, command);

        var batch = Assert.Single(world.Posted);
        Assert.Equal((TaskWorld.Tenant, "key-1"), (batch.TenantId, batch.IdempotencyKey));
        Assert.Equal(new LedgerActor(TaskWorld.Member, LedgerActorType.Human, TaskWorld.Device), batch.Actor);
        var entry = Assert.Single(batch.Entries);
        Assert.Equal(new StockKey(world.Owner.Value, world.From.Value, TaskWorld.Variant, null, StockStatus.Available), entry.From);
        Assert.Equal(new StockKey(world.Owner.Value, world.To.Value, TaskWorld.Variant, null, StockStatus.Available), entry.To);
        Assert.Equal((2m, MovementReason.InternalMove), (entry.Quantity, entry.Reason));
        Assert.Equal(new LedgerDocument("Task", task.Id, task.Lines[0].Id), entry.Document);
        Assert.Equal(command.OccurredAt, entry.OccurredAt);
        Assert.Null(entry.OverrideReason);
        // Parcial: sigue en curso y quien confirma conserva su token.
        Assert.Equal(("InProgress", task.LeaseToken), (dto.Status, dto.LeaseToken));
        Assert.Equal(3m, Assert.Single(dto.Lines).QuantityRemaining);
    }

    // DA-32: la mercancía ya se movió. Se anota sin dejarle a la persona dos tareas en curso.
    [Fact]
    public async Task ALateConfirmationWithAnotherTaskInProgressIsAppliedWithoutResuming()
    {
        var world = new TaskWorld();
        var (old, command) = ClaimedByCaller(world, quantity: 2);
        old.ExpireLease(TaskWorld.Start.AddMinutes(16));
        var current = world.AddTask();
        current.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start.AddMinutes(17));
        world.Now = TaskWorld.Start.AddMinutes(18);

        var dto = await ConfirmAsync(world, command);

        Assert.Equal(("Ready", 2m), (dto.Status, dto.Lines[0].QuantityConfirmed));
        Assert.Null(dto.LeaseToken);
        Assert.Equal(WarehouseTaskStatus.InProgress, current.Status);
        Assert.Single(world.Posted);
        Assert.Contains("save", world.Log);
    }

    // DA-36: la historia de la tarea cuenta cuándo se escaneó, no cuándo llegó.
    [Fact]
    public async Task TheTaskHistoryKeepsTheScanTimeNotTheSyncTime()
    {
        var world = new TaskWorld();
        var (task, command) = ClaimedByCaller(world);
        var scanned = TaskWorld.Start.AddMinutes(1);
        world.Now = TaskWorld.Start.AddMinutes(9);

        await ConfirmAsync(world, command with { OccurredAt = scanned });

        Assert.All(task.Events.TakeLast(2), value => Assert.Equal(scanned, value.OccurredAt));
        Assert.Equal(scanned, task.CompletedAt);
        Assert.Equal(world.Now, task.LastActivityAt);
    }

    [Fact]
    public async Task AScanOutsideTheWindowIs422BeforeTheLedger()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        await AssertDomainAsync(
            world, command with { OccurredAt = TaskWorld.Start.AddDays(-31) }, WmsErrorCodes.MovementOccurredAtOutOfRange);
        await AssertDomainAsync(
            world, command with { OccurredAt = TaskWorld.Start.AddMinutes(6) }, WmsErrorCodes.MovementOccurredAtOutOfRange);
        // La tarea se creó y se publicó en Start: un escaneo anterior a la holgura del reloj (5 minutos) es imposible.
        await AssertDomainAsync(
            world, command with { OccurredAt = TaskWorld.Start.AddMinutes(-6) }, WmsErrorCodes.MovementOccurredAtOutOfRange);
    }

    [Fact]
    public async Task AnUnknownTaskOrLineIs404()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        var task = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => ConfirmAsync(world, command with { TaskId = Guid.NewGuid() }));
        var line = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => ConfirmAsync(world, command with { LineId = Guid.NewGuid() }));

        Assert.Equal(WmsErrorCodes.TaskNotFound, task.Code);
        Assert.Equal(WmsErrorCodes.TaskNotFound, line.Code);
        Assert.Empty(world.Posted);
    }

    [Fact]
    public async Task AStaleTokenIs409LeaseLost()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(
            () => ConfirmAsync(world, command with { LeaseToken = Guid.NewGuid() }));

        Assert.Equal(WmsErrorCodes.TaskLeaseLost, error.Code);
        Assert.False(error.Resumable);
        Assert.Equal(false, error.ProblemExtensions["resumable"]);
        Assert.Empty(world.Posted);
    }

    [Fact]
    public async Task WithoutThePermissionOfTheTypeIs403()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);
        world.Permissions.Remove(WmsPermissions.PutawayPerform);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => ConfirmAsync(world, command));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(world.Posted);
    }

    [Fact]
    public async Task AWrongSourceOrAnotherDestinationWithoutReasonIs422LocationMismatch()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        await AssertDomainAsync(world, command with { FromLocationId = world.Spare.Value }, WmsErrorCodes.TaskLocationMismatch);
        await AssertDomainAsync(world, command with { ToLocationId = world.Spare.Value }, WmsErrorCodes.TaskLocationMismatch);
        await AssertDomainAsync(
            world,
            command with { ToLocationId = world.Spare.Value, OverrideReason = "  " },
            WmsErrorCodes.TaskLocationMismatch);
    }

    [Fact]
    public async Task AnotherDestinationWithReasonGoesToTheMovement()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        await ConfirmAsync(world, command with { ToLocationId = world.Spare.Value, OverrideReason = " lleno " });

        var entry = Assert.Single(Assert.Single(world.Posted).Entries);
        Assert.Equal((world.Spare.Value, "lleno"), (entry.To.LocationId, entry.OverrideReason));
    }

    // Aunque el destino coincida con el planeado, un motivo suelto no se guarda como excepción.
    [Fact]
    public async Task AReasonWithThePlannedDestinationIsNotAnOverride()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        await ConfirmAsync(world, command with { OverrideReason = "nada" });

        Assert.Null(Assert.Single(Assert.Single(world.Posted).Entries).OverrideReason);
    }

    [Fact]
    public async Task AnOverrideDestinationHasToBePhysicalAllowStorageAndDifferFromTheSource()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);
        var virtualOne = world.Warehouse.Locations.First(location => location.IsVirtual).Id.Value;

        foreach (var destination in new[] { virtualOne, world.NoStorage.Value, Guid.NewGuid(), world.From.Value })
        {
            await AssertDomainAsync(
                world,
                command with { ToLocationId = destination, OverrideReason = "lleno" },
                WmsErrorCodes.TaskLocationInvalid);
        }
    }

    [Fact]
    public async Task ALineThatFixesALotOnlyAdmitsThatLot()
    {
        var world = new TaskWorld();
        var lot = LotId.New();
        var (_, command) = ClaimedByCaller(world, lot: lot);

        await AssertDomainAsync(world, command with { LotId = Guid.NewGuid() }, WmsErrorCodes.TaskLotMismatch);
        await AssertDomainAsync(world, command with { LotId = null }, WmsErrorCodes.TaskLotMismatch);
    }

    [Fact]
    public async Task ALineWithoutLotMovesTheScannedOne()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);
        var scanned = Guid.NewGuid();

        await ConfirmAsync(world, command with { LotId = scanned });

        var entry = Assert.Single(Assert.Single(world.Posted).Entries);
        Assert.Equal((scanned, scanned), (entry.From.LotId, entry.To.LotId));
    }

    [Fact]
    public async Task AConfirmationWithoutTheIdempotencyScopeIsAWiringError()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(world, command, key: null));
    }

    [Fact]
    public async Task TheBodiesAreValidatedByField()
    {
        var world = new TaskWorld();
        var (_, command) = ClaimedByCaller(world);

        var error = await Assert.ThrowsAsync<ValidationException>(() => ConfirmAsync(
            world,
            command with { DeviceId = " ", Quantity = 0, OverrideReason = new string('r', 201), OccurredAt = null }));

        Assert.Equal(
            ["DeviceId", "OccurredAt", "OverrideReason", "Quantity"],
            error.Errors.Select(failure => failure.PropertyName).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ADeviceIdOutsideOneToSixtyFourIsAFieldError(string device)
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var error = await Assert.ThrowsAsync<ValidationException>(() => ClaimAsync(world, task.Id, device));

        Assert.Equal("DeviceId", Assert.Single(error.Errors).PropertyName);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("skipped")]
    [InlineData(null)]
    public void AnUnclaimReasonOutsideTheEnumIsInvalid(string? reason) =>
        Assert.Contains(
            new UnclaimTaskValidator().Validate(
                new UnclaimTaskCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reason)).Errors,
            error => error.PropertyName == "Reason");

    [Fact]
    public async Task NextClaimsTheCandidateUnderTheMemberLock()
    {
        var world = new TaskWorld();
        var task = world.AddTask();
        world.Candidate = task.Id;

        var result = await NextAsync(world);

        Assert.NotNull(result.Task);
        Assert.Equal(("InProgress", task.LeaseToken), (result.Task.Status, result.Task.LeaseToken));
        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "candidate", "load-task", "save"], world.Log);
    }

    [Fact]
    public async Task NextWithoutCandidatesReturnsNothing()
    {
        var world = new TaskWorld();

        var result = await NextAsync(world);

        Assert.Null(result.Task);
        Assert.DoesNotContain("save", world.Log);
    }

    // M2: la que ya tiene en curso vuelve con el lease renovado, bajo el lock de la fila, aunque el barrido no haya pasado.
    [Fact]
    public async Task NextRenewsTheTaskAlreadyInProgressInThatWarehouse()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        world.Candidate = world.AddTask().Id;
        world.Now = TaskWorld.Start.AddMinutes(20);

        var result = await NextAsync(world);

        Assert.Equal((task.Id, task.LeaseToken, 2L), (result.Task!.Id, result.Task.LeaseToken, result.Task.Version));
        Assert.Equal(TaskWorld.Start.AddMinutes(35), result.Task.LeaseExpiresAt);
        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "lock-task", "load-task", "save"], world.Log);
    }

    // M2: si el barrido la venció entre la lectura y el bloqueo, «siguiente» se la vuelve a dar a quien la tenía, con el mismo token
    // (DA-34), en lugar de buscar otra.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextGivesBackTheTaskTheSweeperExpiredBetweenTheReadAndTheLock(bool assigned)
    {
        var world = new TaskWorld();
        var task = world.AddTask(assignTo: assigned ? TaskWorld.Member : null);
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start);
        var token = task.LeaseToken;
        world.Candidate = world.AddTask().Id;
        world.Now = TaskWorld.Start.AddMinutes(40);
        world.OnLoadTask = loaded =>
        {
            if (loaded.Id == task.Id)
            {
                loaded.ExpireLease(world.Now);
            }
        };

        var result = await NextAsync(world);

        Assert.Equal((task.Id, "InProgress", token), (result.Task!.Id, result.Task.Status, result.Task.LeaseToken));
        Assert.Equal(TaskWorld.Start.AddMinutes(55), result.Task.LeaseExpiresAt);
        Assert.DoesNotContain("candidate", world.Log);
    }

    // Si el barrido venció la tarea y un supervisor la retuvo, ya no es suya: sigue el camino normal.
    [Fact]
    public async Task NextTakesTheCandidateWhenTheExpiredTaskIsNoLongerTheCallers()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var candidate = world.AddTask();
        world.Candidate = candidate.Id;
        world.Now = TaskWorld.Start.AddMinutes(40);
        world.OnLoadTask = loaded =>
        {
            if (loaded.Id != task.Id)
            {
                return;
            }

            loaded.ExpireLease(world.Now);
            loaded.Hold(TaskWorld.OtherMember, null, world.Now);
        };

        var result = await NextAsync(world);

        Assert.Equal(candidate.Id, result.Task!.Id);
        Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
    }

    // Pendiente del owner: otro celular de la misma persona sobre su propia tarea en curso recibe el token y el reclamo sigue en el
    // celular original. Esta prueba documenta el comportamiento actual; no lo decide.
    [Fact]
    public async Task ClaimOrNextFromAnotherDeviceOfTheSameMemberHandsOverTheTokenAndKeepsTheClaimedDevice()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var token = task.LeaseToken;

        var claimed = await ClaimAsync(world, task.Id, "otro-celular");
        var next = await NextAsync(world, device: "otro-celular");

        Assert.Equal(token, claimed.LeaseToken);
        Assert.Equal(token, next.Task!.LeaseToken);
        Assert.Equal(TaskWorld.Device, task.ClaimedDeviceId);
    }

    [Fact]
    public async Task NextWithATaskInAnotherWarehouseIs409MemberBusyWithItsId()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);

        var error = await Assert.ThrowsAsync<TaskMemberBusyException>(() => NextAsync(world, Guid.NewGuid()));

        Assert.Equal(task.Id, error.TaskId);
    }

    [Fact]
    public async Task NextWithoutAnyTypePermissionIs403()
    {
        var world = new TaskWorld();
        world.Permissions.Remove(WmsPermissions.PutawayPerform);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => NextAsync(world));
    }

    [Fact]
    public async Task ClaimingWithAnotherTaskInProgressNamesIt()
    {
        var world = new TaskWorld();
        var (current, _) = ClaimedByCaller(world);
        var other = world.AddTask();

        var error = await Assert.ThrowsAsync<TaskMemberBusyException>(() => ClaimAsync(world, other.Id));

        Assert.Equal(current.Id, error.TaskId);
        Assert.Equal(WarehouseTaskStatus.Ready, other.Status);
    }

    [Fact]
    public async Task ClaimingTheTaskAlreadyInProgressRenewsTheLeaseWithTheSameToken()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        world.Now = TaskWorld.Start.AddMinutes(20);

        var dto = await ClaimAsync(world, task.Id);

        Assert.Equal((task.LeaseToken, 2L), (dto.LeaseToken, dto.Version));
        Assert.Equal(TaskWorld.Start.AddMinutes(35), dto.LeaseExpiresAt);
        Assert.Contains("save", world.Log);
    }

    [Fact]
    public async Task ClaimingAnUnknownTaskIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => ClaimAsync(new TaskWorld(), Guid.NewGuid()));

        Assert.Equal(WmsErrorCodes.TaskNotFound, error.Code);
    }

    [Fact]
    public async Task AHeartbeatOnlyAcceptsTheCurrentLease()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var token = task.LeaseToken!.Value;
        var handler = new HeartbeatTaskHandler(
            world.Context(), world.Authorization, world.Caller(), world.UnitOfWork(), world.Repository(), Guard(world),
            world.Clock());
        world.Now = TaskWorld.Start.AddMinutes(2);

        var lease = await handler.HandleAsync(new HeartbeatTaskCommand(TaskWorld.Tenant, task.Id, token), Ct);
        task.ExpireLease(TaskWorld.Start.AddMinutes(30));
        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(
            () => handler.HandleAsync(new HeartbeatTaskCommand(TaskWorld.Tenant, task.Id, token), Ct));

        Assert.Equal(TaskWorld.Start.AddMinutes(17), lease.LeaseExpiresAt);
        // Venció y nadie más la tomó: la app puede pedirla otra vez con claim.
        Assert.True(error.Resumable);
        // Reanudable no alcanza: sólo una confirmación reanuda.
        Assert.Equal(WmsErrorCodes.TaskLeaseLost, error.Code);
    }

    // La app trata como éxito un unclaim repetido: la tarea ya no es suya y no se puede retomar con ese token.
    [Fact]
    public async Task RepeatingAnAppliedUnclaimIs409NotResumable()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var token = task.LeaseToken!.Value;
        var handler = new UnclaimTaskHandler(
            world.Context(), world.Authorization, world.Caller(), world.UnitOfWork(), world.Repository(), Guard(world),
            world.Clock(), world.Composer(), new UnclaimTaskValidator());
        var command = new UnclaimTaskCommand(TaskWorld.Tenant, task.Id, token, "ShiftEnd");
        await handler.HandleAsync(command, Ct);

        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(() => handler.HandleAsync(command, Ct));

        Assert.Equal(WmsErrorCodes.TaskLeaseLost, error.Code);
        Assert.False(error.Resumable);
    }

    [Fact]
    public async Task UnclaimingReturnsTheTaskWithoutAToken()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var handler = new UnclaimTaskHandler(
            world.Context(), world.Authorization, world.Caller(), world.UnitOfWork(), world.Repository(), Guard(world),
            world.Clock(), world.Composer(), new UnclaimTaskValidator());

        var dto = await handler.HandleAsync(
            new UnclaimTaskCommand(TaskWorld.Tenant, task.Id, task.LeaseToken!.Value, "ShiftEnd"), Ct);

        Assert.Equal("Ready", dto.Status);
        Assert.Null(dto.LeaseToken);
        Assert.Contains("save", world.Log);
    }

    [Fact]
    public async Task MineShowsTheCurrentAndTheAssigned()
    {
        var world = new TaskWorld();
        var (current, _) = ClaimedByCaller(world);
        var assigned = world.AddTask(assignTo: TaskWorld.Member);
        world.AddTask(assignTo: TaskWorld.OtherMember);
        var handler = new GetMyTasksHandler(
            world.Context(), world.Authorization, world.Caller(), world.Repository(), world.Composer());

        var mine = await handler.HandleAsync(new GetMyTasksQuery(TaskWorld.Tenant, world.Warehouse.Id.Value), Ct);

        Assert.Equal((current.Id, current.LeaseToken), (mine.Current!.Id, mine.Current.LeaseToken));
        Assert.Equal(assigned.Id, Assert.Single(mine.Assigned).Id);
    }
}
