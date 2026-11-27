using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class WarehouseTaskLeaseTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Supervisor = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expired = Now.AddMinutes(16);
    private const string Device = "zebra-01";

    private static WarehouseTaskLine Line(decimal qty = 5) =>
        WarehouseTaskLine.Create(Guid.NewGuid(), null, StockStatus.Available, LocationId.New(), LocationId.New(), qty);

    private static WarehouseTask NewMove(bool publish = true, Guid? assignTo = null, params WarehouseTaskLine[] lines) =>
        WarehouseTask.CreateMove(
            Tenant, "TSK-20261126-0001", WarehouseId.New(), OwnerId.New(), null, null, null,
            lines.Length == 0 ? [Line()] : lines, assignTo, publish, Supervisor, Now);

    /// <summary>Reclamada por <see cref="Member"/> en <see cref="Now"/>; devuelve el token vigente.</summary>
    private static (WarehouseTask Task, Guid Token) Claimed(Guid? assignTo = null, params WarehouseTaskLine[] lines)
    {
        var task = NewMove(true, assignTo, lines);
        task.Claim(Member, Device, Now);
        return (task, task.LeaseToken!.Value);
    }

    [Fact]
    public void ClaimingAReadyTaskStartsALeaseOfFifteenMinutes()
    {
        var task = NewMove();

        task.Claim(Member, Device, Now);

        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
        Assert.Equal(Member, task.ClaimedByMemberId);
        Assert.Equal(Device, task.ClaimedDeviceId);
        Assert.NotNull(task.LeaseToken);
        Assert.NotEqual(Guid.Empty, task.LeaseToken);
        Assert.Equal(Now.AddMinutes(15), task.LeaseExpiresAt);
        Assert.Equal(Now, task.LastActivityAt);
        Assert.Null(task.AssignedMemberId);
        Assert.Equal(2, task.Version);
        var claimed = task.Events[^1];
        Assert.Equal(TaskEventKind.Claimed, claimed.Kind);
        Assert.Equal(Member, claimed.MemberId);
        Assert.Equal(Device, claimed.DeviceId);
    }

    [Fact]
    public void TheAssigneeCanClaimAnAssignedTask()
    {
        var (task, _) = Claimed(assignTo: Member);

        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
        Assert.Equal(Member, task.AssignedMemberId);
    }

    [Fact]
    public void ClaimingATaskAssignedToSomeoneElseIsRejected()
    {
        var task = NewMove(assignTo: Other);

        var error = Assert.Throws<WmsDomainException>(() => task.Claim(Member, Device, Now));

        Assert.Equal(WmsErrorCodes.TaskAssignedToOther, error.Code);
        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Null(task.LeaseToken);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public void ClaimingAPlannedTaskIsAnInvalidTransition()
    {
        var error = Assert.Throws<TaskTransitionException>(() => NewMove(publish: false).Claim(Member, Device, Now));

        Assert.Equal("Planned", error.ProblemExtensions["from"]);
        Assert.Equal("claim", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ClaimingATaskInProgressIsAnInvalidTransition()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<TaskTransitionException>(() => task.Claim(Other, Device, Now));

        Assert.Equal("InProgress", error.ProblemExtensions["from"]);
        Assert.Equal(token, task.LeaseToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ADeviceIdHasBetweenOneAndSixtyFourCharacters(string deviceId)
    {
        var task = NewMove();

        Assert.Throws<ArgumentException>(() => task.Claim(Member, deviceId, Now));
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
    }

    [Fact]
    public void TheCurrentTokenOfTheSameMemberIsValid()
    {
        var (task, token) = Claimed();

        Assert.Equal(LeaseCheck.Valid, task.CheckLease(Member, token));
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, Guid.NewGuid()));
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Other, token));
    }

    [Fact]
    public void AnUnclaimedTaskHasNoLeaseForAnyone() =>
        Assert.Equal(LeaseCheck.Lost, NewMove().CheckLease(Member, Guid.NewGuid()));

    [Fact]
    public void AfterExpiringTheLastClaimerCanResume()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        Assert.Equal(LeaseCheck.Resumable, task.CheckLease(Member, token));
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Other, token));
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, Guid.NewGuid()));
    }

    [Fact]
    public void ATaskClaimedLaterBySomeoneElseIsLostForThePreviousClaimer()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Claim(Other, "zebra-02", Expired);

        Assert.NotEqual(token, task.LeaseToken);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    // DA-34: las confirmaciones que la app dejó en cola con ese token siguen valiendo.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReclaimingMyExpiredTaskOnTheSameDeviceKeepsTheToken(bool assigned)
    {
        var (task, token) = Claimed(assignTo: assigned ? Member : null);
        task.ExpireLease(Expired);

        task.Claim(Member, $" {Device} ", Expired);

        Assert.Equal(token, task.LeaseToken);
        Assert.Equal(LeaseCheck.Valid, task.CheckLease(Member, token));
        Assert.Equal(Expired.AddMinutes(15), task.LeaseExpiresAt);
        Assert.Equal(4, task.Version);
        Assert.Equal(TaskEventKind.Claimed, task.Events[^1].Kind);
    }

    [Fact]
    public void ReclaimingMyExpiredTaskOnAnotherDeviceChangesTheToken()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Claim(Member, "zebra-02", Expired);

        Assert.NotEqual(token, task.LeaseToken);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    [Fact]
    public void ReclaimingAfterSomeoneElseHadItChangesTheTokenEvenOnTheSameDevice()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);
        task.Claim(Other, "zebra-02", Expired);
        task.ExpireLease(Expired.AddMinutes(16));

        task.Claim(Member, Device, Expired.AddMinutes(17));

        Assert.NotEqual(token, task.LeaseToken);
    }

    // Soltar limpia el reclamo: volver a tomarla es un reclamo nuevo.
    [Fact]
    public void ReclaimingAfterUnclaimingChangesTheToken()
    {
        var (task, token) = Claimed();
        task.Unclaim(Member, token, TaskUnclaimReason.Other, Now);

        task.Claim(Member, Device, Now);

        Assert.NotEqual(token, task.LeaseToken);
    }

    [Fact]
    public void ACancelledTaskIsLostForItsClaimer()
    {
        var (task, token) = Claimed();

        task.Cancel("pedido anulado", Supervisor, Now);

        Assert.Equal(WarehouseTaskStatus.Cancelled, task.Status);
        Assert.Null(task.ClaimedByMemberId);
        Assert.Null(task.ClaimedDeviceId);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    [Fact]
    public void ADoneTaskIsLostForItsClaimerAndClearsTheLease()
    {
        var (task, token) = Claimed();

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, Now);

        Assert.Null(task.ClaimedByMemberId);
        Assert.Null(task.ClaimedDeviceId);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    // M2: pedir otra vez la tarea en curso renueva el lease con el mismo token, como el latido: sin versión ni evento.
    [Fact]
    public void RenewingMyClaimExtendsTheLeaseWithTheSameTokenWithoutVersionOrEvent()
    {
        var (task, token) = Claimed();
        var events = task.Events.Count;

        // Vencido pero sin barrer: sigue InProgress y nadie más pudo tomarlo.
        task.RenewClaim(Member, Expired);

        Assert.Equal((WarehouseTaskStatus.InProgress, token, 2L), (task.Status, task.LeaseToken!.Value, task.Version));
        Assert.Equal(Expired.AddMinutes(15), task.LeaseExpiresAt);
        Assert.Equal(Expired, task.LastActivityAt);
        Assert.Equal(events, task.Events.Count);
    }

    [Fact]
    public void OnlyTheClaimerRenewsATaskInProgress()
    {
        var (task, _) = Claimed();

        Assert.Throws<InvalidOperationException>(() => task.RenewClaim(Other, Expired));
        Assert.Throws<TaskTransitionException>(() => NewMove().RenewClaim(Member, Now));
    }

    [Fact]
    public void AHeartbeatExtendsTheLeaseWithoutVersionOrEvent()
    {
        var (task, token) = Claimed();
        var events = task.Events.Count;

        task.Heartbeat(Member, token, Now.AddMinutes(10));

        Assert.Equal(Now.AddMinutes(25), task.LeaseExpiresAt);
        Assert.Equal(Now.AddMinutes(10), task.LastActivityAt);
        Assert.Equal(2, task.Version);
        Assert.Equal(events, task.Events.Count);
    }

    [Fact]
    public void AHeartbeatNeedsTheCurrentLease()
    {
        var (task, token) = Claimed();

        Assert.Throws<InvalidOperationException>(() => task.Heartbeat(Other, token, Now));
        task.ExpireLease(Expired);
        // Reanudable no alcanza: sólo una confirmación reanuda.
        Assert.Throws<InvalidOperationException>(() => task.Heartbeat(Member, token, Expired));

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Null(task.LeaseExpiresAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnclaimingReturnsTheTaskToThePoolWithoutAssignee(bool assigned)
    {
        var (task, token) = Claimed(assignTo: assigned ? Member : null);

        task.Unclaim(Member, token, TaskUnclaimReason.CannotReach, Now.AddMinutes(2));

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Null(task.AssignedMemberId);
        Assert.Null(task.ClaimedByMemberId);
        Assert.Null(task.ClaimedDeviceId);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(Now, task.ReleasedAt);
        Assert.Equal(3, task.Version);
        var unclaimed = task.Events[^1];
        Assert.Equal(TaskEventKind.Unclaimed, unclaimed.Kind);
        Assert.Equal(Member, unclaimed.MemberId);
        Assert.Equal(Device, unclaimed.DeviceId);
        Assert.Equal("CannotReach", unclaimed.Detail);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    [Fact]
    public void UnclaimingNeedsTheCurrentLease()
    {
        var (task, _) = Claimed();

        Assert.Throws<InvalidOperationException>(
            () => task.Unclaim(Member, Guid.NewGuid(), TaskUnclaimReason.Other, Now));
        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
    }

    [Fact]
    public void AnExpiredLeaseReturnsTheTaskToThePoolKeepingTheClaim()
    {
        var (task, token) = Claimed();

        Assert.True(task.ExpireLease(Expired));

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(Member, task.ClaimedByMemberId);
        Assert.Equal(token, task.LeaseToken);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(Now, task.ReleasedAt);
        Assert.Equal(3, task.Version);
        var expired = task.Events[^1];
        Assert.Equal(TaskEventKind.LeaseExpired, expired.Kind);
        Assert.Null(expired.MemberId);
    }

    [Fact]
    public void AnExpiredAssignedClaimGoesBackToItsAssignee()
    {
        var (task, token) = Claimed(assignTo: Member);

        Assert.True(task.ExpireLease(Expired));

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(Member, task.AssignedMemberId);
        Assert.Equal(LeaseCheck.Resumable, task.CheckLease(Member, token));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    public void ALiveLeaseIsUntouched(int minutes)
    {
        var (task, _) = Claimed();

        Assert.False(task.ExpireLease(Now.AddMinutes(minutes)));

        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
        Assert.Equal(Now.AddMinutes(15), task.LeaseExpiresAt);
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public void ExpiringTwiceChangesNothingTheSecondTime()
    {
        var (task, _) = Claimed();
        task.ExpireLease(Expired);

        Assert.False(task.ExpireLease(Expired.AddMinutes(1)));
        Assert.Equal(3, task.Version);
    }

    [Fact]
    public void APartialConfirmationKeepsTheLineOpenAndRenewsTheLease()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;

        var line = task.ConfirmLine(Member, token, Device, lineId, 2, Now.AddMinutes(3));

        Assert.Same(task.Lines[0], line);
        Assert.Equal(TaskLineStatus.Open, line.Status);
        Assert.Equal(2, line.QuantityConfirmed);
        Assert.Equal(3, line.Remaining);
        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
        Assert.Equal(Now.AddMinutes(18), task.LeaseExpiresAt);
        Assert.Equal(Now.AddMinutes(3), task.LastActivityAt);
        Assert.Equal(3, task.Version);
        var confirmed = task.Events[^1];
        Assert.Equal(TaskEventKind.LineConfirmed, confirmed.Kind);
        Assert.Equal(lineId, confirmed.LineId);
        Assert.Equal(2, confirmed.Quantity);
        Assert.Equal(Member, confirmed.MemberId);
        Assert.Equal(Device, confirmed.DeviceId);
    }

    [Fact]
    public void ConfirmingTheRestClosesTheLineAndTheTask()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;
        task.ConfirmLine(Member, token, Device, lineId, 2, Now);

        task.ConfirmLine(Member, token, Device, lineId, 3, Now.AddMinutes(4));

        Assert.Equal(TaskLineStatus.Done, task.Lines[0].Status);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
        Assert.Equal(Now.AddMinutes(4), task.CompletedAt);
        Assert.Null(task.CancelledAt);
        // Cerrar la tarea no es otra transición: va en la misma versión que la confirmación.
        Assert.Equal(4, task.Version);
        Assert.Equal(
            [TaskEventKind.LineConfirmed, TaskEventKind.Completed],
            task.Events.TakeLast(2).Select(e => e.Kind));
        Assert.Equal(Member, task.Events[^1].MemberId);
    }

    [Fact]
    public void ConfirmingMoreThanRemainsIsRejected()
    {
        var (task, token) = Claimed();
        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 2, Now);

        var error = Assert.Throws<WmsDomainException>(
            () => task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 3.000001m, Now));

        Assert.Equal(WmsErrorCodes.TaskQuantityExceedsRemaining, error.Code);
        Assert.Equal(2, task.Lines[0].QuantityConfirmed);
        Assert.Equal(3, task.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AConfirmedQuantityMustBePositive(int quantity)
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.ConfirmLine(Member, token, Device, task.Lines[0].Id, quantity, Now));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
    }

    [Fact]
    public void ConfirmingAClosedLineIsAnInvalidTransition()
    {
        var (task, token) = Claimed(null, Line(), Line());
        var lineId = task.Lines[0].Id;
        task.ConfirmLine(Member, token, Device, lineId, 5, Now);

        var error = Assert.Throws<TaskTransitionException>(
            () => task.ConfirmLine(Member, token, Device, lineId, 1, Now));

        Assert.Equal("Done", error.ProblemExtensions["from"]);
        Assert.Equal("confirm-line", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ALateConfirmationResumesTheTaskWithTheSameToken()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 2, Expired.AddMinutes(1));

        Assert.Equal(WarehouseTaskStatus.InProgress, task.Status);
        Assert.Equal(token, task.LeaseToken);
        Assert.Equal(Expired.AddMinutes(16), task.LeaseExpiresAt);
        Assert.Equal(4, task.Version);
        Assert.Equal(
            [TaskEventKind.LeaseExpired, TaskEventKind.LeaseResumed, TaskEventKind.LineConfirmed],
            task.Events.TakeLast(3).Select(e => e.Kind));
    }

    [Fact]
    public void ARejectedLateConfirmationDoesNotResumeTheTask()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        Assert.Throws<WmsDomainException>(
            () => task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 6, Expired));

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(3, task.Version);
        Assert.Equal(TaskEventKind.LeaseExpired, task.Events[^1].Kind);
    }

    // DA-32: quien ya tiene otra tarea en curso no puede recibir esta de vuelta, pero lo que movió se anota.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALateConfirmationOfABusyMemberLeavesTheTaskWhereItIs(bool assigned)
    {
        var (task, token) = Claimed(assignTo: assigned ? Member : null);
        task.ExpireLease(Expired);
        var status = task.Status;
        var synced = Expired.AddMinutes(1);

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 2, synced, memberIsBusy: true);

        Assert.Equal(status, task.Status);
        Assert.Equal(2, task.Lines[0].QuantityConfirmed);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(token, task.LeaseToken);
        Assert.Equal(synced, task.LastActivityAt);
        Assert.Equal(4, task.Version);
        Assert.Equal(
            [TaskEventKind.LeaseExpired, TaskEventKind.LineConfirmed], task.Events.TakeLast(2).Select(e => e.Kind));
        // Lo que sigue en la cola de la app se sigue aceptando.
        Assert.Equal(LeaseCheck.Resumable, task.CheckLease(Member, token));
    }

    [Fact]
    public void ALateConfirmationOfABusyMemberCanFinishTheTask()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, Expired, memberIsBusy: true);

        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
        Assert.Equal(Expired, task.CompletedAt);
        Assert.Null(task.LeaseToken);
        Assert.DoesNotContain(task.Events, e => e.Kind == TaskEventKind.LeaseResumed);
    }

    // DA-36: los hechos de una confirmación llevan la hora del escaneo; el lease y la actividad, la del servidor.
    [Fact]
    public void ALateSyncKeepsTheScanTimeInTheHistory()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);
        var scanned = Now.AddMinutes(5).ToOffset(TimeSpan.FromHours(-5));
        var synced = Expired.AddMinutes(20);

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, synced, scanned);

        Assert.Equal(
            [TaskEventKind.LeaseResumed, TaskEventKind.LineConfirmed, TaskEventKind.Completed],
            task.Events.TakeLast(3).Select(e => e.Kind));
        Assert.All(task.Events.TakeLast(3), e =>
        {
            Assert.Equal(scanned, e.OccurredAt);
            // timestamptz sólo admite offset cero.
            Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        });
        Assert.Equal(scanned, task.CompletedAt);
        Assert.Equal(synced, task.LastActivityAt);
    }

    [Theory]
    [InlineData(-43201)]
    [InlineData(6)]
    public void AScanOutsideTheLedgerWindowIsRejectedWithoutResuming(int minutes)
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        var error = Assert.Throws<WmsDomainException>(() => task.ConfirmLine(
            Member, token, Device, task.Lines[0].Id, 1, Expired, Expired.AddMinutes(minutes)));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(0, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void ConfirmingWithALostLeaseIsAWiringError()
    {
        var (task, _) = Claimed();

        Assert.Throws<InvalidOperationException>(
            () => task.ConfirmLine(Member, Guid.NewGuid(), Device, task.Lines[0].Id, 1, Now));
        Assert.Equal(0, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void ReassigningATaskInProgressNeedsAReason()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(() => task.Assign(Other, Supervisor, "  ", Now));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
        Assert.Equal(LeaseCheck.Valid, task.CheckLease(Member, token));
        Assert.Equal(2, task.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReassigningATaskInProgressRevokesTheLease(bool toSomeoneElse)
    {
        var (task, token) = Claimed();
        var assignee = toSomeoneElse ? Other : Member;

        task.Assign(assignee, Supervisor, "cambio de turno", Now);

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(assignee, task.AssignedMemberId);
        Assert.Null(task.ClaimedByMemberId);
        Assert.Null(task.ClaimedDeviceId);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.LeaseExpiresAt);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
        Assert.Equal(3, task.Version);
        Assert.Equal("cambio de turno", task.Events[^1].Detail);
    }

    [Fact]
    public void AssigningAnExpiredTaskToSomeoneElseRevokesTheLateConfirmation()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Assign(Other, Supervisor, null, Expired);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));

        // Quitar después la asignación no revive el token revocado.
        task.Unassign(Supervisor, Expired);
        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
        Assert.Null(task.ClaimedByMemberId);
    }

    [Fact]
    public void AssigningAnExpiredTaskToItsClaimerKeepsItResumable()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Assign(Member, Supervisor, null, Expired);

        Assert.Equal(LeaseCheck.Resumable, task.CheckLease(Member, token));
    }

    // B1: retener borra el reclamo que dejó un lease vencido; si no, al volver a publicar la confirmación tardía de antes la reanudaría.
    [Fact]
    public void HoldingAndPublishingAgainRevokesTheExpiredClaim()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Hold(Supervisor, null, Expired);
        task.Publish(Supervisor, Expired.AddMinutes(1));

        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
        Assert.Null(task.ClaimedByMemberId);
        Assert.Null(task.ClaimedDeviceId);
        Assert.Null(task.LeaseToken);
        task.Claim(Member, Device, Expired.AddMinutes(2));
        Assert.NotEqual(token, task.LeaseToken);
    }

    // B2: nadie escanea una tarea antes de que exista en el pool.
    [Fact]
    public void AScanEarlierThanTheReleaseIsRejectedWithoutResuming()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        var error = Assert.Throws<WmsDomainException>(() => task.ConfirmLine(
            Member, token, Device, task.Lines[0].Id, 1, Expired, Now.AddSeconds(-1)));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(0, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void AScanAtTheReleaseInstantIsAccepted()
    {
        var (task, token) = Claimed();

        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 1, Now.AddMinutes(1), Now);

        Assert.Equal(1, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void AHeldTaskIsLostForItsLastClaimer()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.Hold(Supervisor, null, Expired);

        Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("hold")]
    [InlineData("unassign")]
    public void ATaskInProgressAdmitsOnlyReassigningPriorityAndCancelling(string action)
    {
        var (task, _) = Claimed(assignTo: Member);
        Action run = action switch
        {
            "publish" => () => task.Publish(Supervisor, Now),
            "hold" => () => task.Hold(Supervisor, null, Now),
            _ => () => task.Unassign(Supervisor, Now),
        };

        var error = Assert.Throws<TaskTransitionException>(run);

        Assert.Equal("InProgress", error.ProblemExtensions["from"]);
        Assert.Equal(action, error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ChangingThePriorityOfADoneTaskIsAnInvalidTransition()
    {
        var (task, token) = Claimed();
        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, Now);

        var error = Assert.Throws<TaskTransitionException>(() => task.ChangePriority(5, false, Supervisor, Now));

        Assert.Equal("Done", error.ProblemExtensions["from"]);
        Assert.Equal("change-priority", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void CancellingTheLastOpenLineAfterAConfirmedOneCompletesTheTask()
    {
        var (task, token) = Claimed(null, Line(), Line());
        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, Now);

        task.CancelLine(task.Lines[1].Id, "sin stock", Supervisor, Now.AddMinutes(1));

        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
        Assert.Equal(Now.AddMinutes(1), task.CompletedAt);
        Assert.Null(task.CancelledAt);
        Assert.Null(task.CancelReason);
        Assert.Null(task.LeaseToken);
        Assert.Equal(4, task.Version);
        Assert.Equal([TaskEventKind.LineCancelled, TaskEventKind.Completed], task.Events.TakeLast(2).Select(e => e.Kind));
    }

    [Fact]
    public void CancellingAPartiallyConfirmedOnlyLineCancelsTheTaskAndKeepsWhatWasConfirmed()
    {
        var (task, token) = Claimed();
        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 2, Now);

        task.CancelLine(task.Lines[0].Id, "sin stock", Supervisor, Now);

        // Spec §6.3: cuenta el estado de las líneas, y ninguna quedó Done.
        Assert.Equal(WarehouseTaskStatus.Cancelled, task.Status);
        Assert.Equal(2, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void CancellingATaskKeepsItsConfirmedLinesAsDone()
    {
        var (task, token) = Claimed(null, Line(), Line());
        task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 5, Now);

        task.Cancel("se acabó el turno", Supervisor, Now);

        Assert.Equal(WarehouseTaskStatus.Cancelled, task.Status);
        Assert.Equal([TaskLineStatus.Done, TaskLineStatus.Cancelled], task.Lines.Select(line => line.Status));
        Assert.Null(task.Lines[0].CancelReason);
    }
}
