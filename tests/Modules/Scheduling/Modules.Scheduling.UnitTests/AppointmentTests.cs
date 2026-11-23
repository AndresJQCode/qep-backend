using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Citas»: Pending → Confirmed | Cancelled; Confirmed → Cancelled | Completed | NoShow; los tres últimos son
// terminales. Completed y NoShow sólo después de la hora de inicio. Reprogramar conserva el id y el estado.
public sealed class AppointmentTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);

    private static Appointment NewAppointment(AppointmentStatus status = AppointmentStatus.Confirmed, AppointmentSource source = AppointmentSource.BackOffice) =>
        Appointment.Create(
            AppointmentId.New(), Tenant, LocationId.New(), ResourceId.New(), ServiceId.New(), Guid.CreateVersion7(), SessionId.New(),
            blockId: null, Start, Start.AddMinutes(30), status, source, conversationId: null, note: "  Primera vez ", Actor, Created);

    [Fact]
    public void ACreatedAppointmentKeepsItsStatusSourceAndLeavesACreatedEvent()
    {
        var appointment = NewAppointment(AppointmentStatus.Pending, AppointmentSource.Channel);

        Assert.Equal((AppointmentStatus.Pending, AppointmentSource.Channel), (appointment.Status, appointment.Source));
        Assert.Equal("Primera vez", appointment.Note);
        Assert.Equal(1, appointment.Version);
        var created = Assert.Single(appointment.Events);
        Assert.Equal((AppointmentAction.Created, Actor, Created), (created.Action, created.ActorMemberId, created.At));
    }

    [Fact]
    public void AnAppointmentKeepsTheClientRequestIdItWasBookedWith()
    {
        var appointment = Appointment.Create(
            AppointmentId.New(), Tenant, LocationId.New(), ResourceId.New(), ServiceId.New(), Guid.CreateVersion7(), SessionId.New(),
            blockId: null, Start, Start.AddMinutes(30), AppointmentStatus.Pending, AppointmentSource.Channel, conversationId: null,
            note: null, actorMemberId: null, Created, clientRequestId: "  wa-123 ");

        Assert.Equal("wa-123", appointment.ClientRequestId);
        Assert.Null(NewAppointment().ClientRequestId);
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.NoShow)]
    public void AnAppointmentCannotBeBornInATerminalState(AppointmentStatus status) =>
        Assert.Throws<ArgumentException>(() => NewAppointment(status));

    [Fact]
    public void APendingAppointmentIsConfirmedOnce()
    {
        var appointment = NewAppointment(AppointmentStatus.Pending);

        appointment.Confirm(Actor, Created.AddMinutes(5));

        Assert.Equal(AppointmentStatus.Confirmed, appointment.Status);
        Assert.Equal(AppointmentAction.Confirmed, appointment.Events[^1].Action);
        Assert.Equal(2, appointment.Version);
        AssertInvalid(() => appointment.Confirm(Actor, Created.AddMinutes(6)));
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    public void AnOpenAppointmentCanBeCancelledWithItsReason(AppointmentStatus status)
    {
        var appointment = NewAppointment(status);

        appointment.Cancel("  El cliente no puede ir ", Actor, Created.AddMinutes(5));

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal("El cliente no puede ir", appointment.CancelReason);
        Assert.Equal(AppointmentAction.Cancelled, appointment.Events[^1].Action);
    }

    [Fact]
    public void ACancelledAppointmentCannotBeConfirmedMovedOrMarked()
    {
        var appointment = NewAppointment();
        appointment.Cancel(null, Actor, Created.AddMinutes(1));

        AssertInvalid(() => appointment.Confirm(Actor, Start.AddHours(1)));
        AssertInvalid(() => appointment.MarkAttended(Actor, Start.AddHours(1)));
        AssertInvalid(() => appointment.MarkNoShow(Actor, Start.AddHours(1)));
        AssertInvalid(() => appointment.Cancel(null, Actor, Start.AddHours(1)));
        AssertInvalid(() => appointment.Reschedule(ResourceId.New(), SessionId.New(), Start.AddDays(1), Start.AddDays(1).AddMinutes(30), Actor, Start));
    }

    [Fact]
    public void OnlyAConfirmedAppointmentCanBeMarkedAndOnlyAfterItsStart()
    {
        var pending = NewAppointment(AppointmentStatus.Pending);
        AssertInvalid(() => pending.MarkAttended(Actor, Start.AddHours(1)));

        var confirmed = NewAppointment();
        var early = Assert.Throws<SchedulingDomainException>(() => confirmed.MarkAttended(Actor, Start.AddMinutes(-1)));
        Assert.Equal(SchedulingErrorCodes.BeforeStart, early.Code);

        confirmed.MarkAttended(Actor, Start);
        Assert.Equal(AppointmentStatus.Completed, confirmed.Status);
        AssertInvalid(() => confirmed.MarkNoShow(Actor, Start.AddHours(1)));
    }

    [Fact]
    public void ANoShowIsMarkedAfterTheStartAndIsTerminal()
    {
        var appointment = NewAppointment();

        var early = Assert.Throws<SchedulingDomainException>(() => appointment.MarkNoShow(Actor, Start.AddMinutes(-5)));
        Assert.Equal(SchedulingErrorCodes.BeforeStart, early.Code);
        appointment.MarkNoShow(Actor, Start.AddMinutes(45));

        Assert.Equal(AppointmentStatus.NoShow, appointment.Status);
        AssertInvalid(() => appointment.Cancel(null, Actor, Start.AddHours(2)));
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    public void RescheduleKeepsTheIdAndTheStatusAndLeavesAnEvent(AppointmentStatus status)
    {
        var appointment = NewAppointment(status);
        var id = appointment.Id;
        var newResource = ResourceId.New();
        var newSession = SessionId.New();
        var newStart = Start.AddDays(2);

        appointment.Reschedule(newResource, newSession, newStart, newStart.AddMinutes(30), Actor, Created.AddMinutes(10));

        Assert.Equal((id, status), (appointment.Id, appointment.Status));
        Assert.Equal((newResource, newSession, newStart, newStart.AddMinutes(30)),
            (appointment.ResourceId, appointment.SessionId, appointment.StartsAt, appointment.EndsAt));
        Assert.Equal(AppointmentAction.Rescheduled, appointment.Events[^1].Action);
    }

    [Fact]
    public void RescheduleClearsTheWholeReminderStateSoTheNewTimeIsRemindedAgain()
    {
        var sent = NewAppointment(AppointmentStatus.Confirmed);
        sent.RecordReminderSent(Created.AddHours(1));
        var skipped = NewAppointment(AppointmentStatus.Confirmed);
        skipped.RecordReminderSkipped("no_phone", Created.AddHours(1));

        foreach (var appointment in new[] { sent, skipped })
        {
            appointment.Reschedule(ResourceId.New(), SessionId.New(), Start.AddDays(2), Start.AddDays(2).AddMinutes(30), Actor, Created.AddHours(2));

            Assert.Null(appointment.ReminderSentAt);
            Assert.Null(appointment.ReminderSkippedReason);
            Assert.Null(appointment.ReminderClaimedUntil);
            Assert.Null(appointment.ReminderLastError);
            Assert.Equal(0, appointment.ReminderAttempts);
            Assert.Equal(AppointmentAction.Rescheduled, appointment.Events[^1].Action);
        }
    }

    [Fact]
    public void ARescheduleToABackwardsRangeIsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            NewAppointment().Reschedule(ResourceId.New(), SessionId.New(), Start, Start.AddMinutes(-10), Actor, Created));

    [Fact]
    public void TheReminderIsRecordedOnceAndOnlyForAnOpenAppointment()
    {
        var appointment = NewAppointment();
        var version = appointment.Version;

        appointment.RecordReminderSent(Created.AddHours(1));

        Assert.Equal(Created.AddHours(1), appointment.ReminderSentAt);
        // A system write: an open tab holding this appointment must not get a 412 because the sweeper noted the reminder.
        Assert.Equal(version, appointment.Version);
        Assert.Equal(AppointmentAction.ReminderSent, appointment.Events[^1].Action);
        Assert.Throws<InvalidOperationException>(() => appointment.RecordReminderSent(Created.AddHours(2)));

        var cancelled = NewAppointment();
        cancelled.Cancel(null, Actor, Created.AddMinutes(1));
        AssertInvalid(() => cancelled.RecordReminderSent(Created.AddHours(1)));
    }

    [Fact]
    public void ASkippedReminderIsFinalAndLeavesItsReasonInTheHistory()
    {
        var appointment = NewAppointment();
        var version = appointment.Version;

        appointment.RecordReminderSkipped("no_phone", Created.AddHours(1));

        Assert.Equal(version, appointment.Version);
        Assert.Equal("no_phone", appointment.ReminderSkippedReason);
        Assert.Null(appointment.ReminderSentAt);
        var entry = appointment.Events[^1];
        Assert.Equal((AppointmentAction.ReminderSkipped, "no_phone", (Guid?)null), (entry.Action, entry.Detail, entry.ActorMemberId));
        // Ni se omite dos veces ni se envía después de omitida: el motivo es la última palabra.
        Assert.Throws<InvalidOperationException>(() => appointment.RecordReminderSkipped("no_phone", Created.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => appointment.RecordReminderSent(Created.AddHours(2)));

        var sent = NewAppointment();
        sent.RecordReminderSent(Created.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => sent.RecordReminderSkipped("no_phone", Created.AddHours(2)));

        var cancelled = NewAppointment();
        cancelled.Cancel(null, Actor, Created.AddMinutes(1));
        AssertInvalid(() => cancelled.RecordReminderSkipped("no_phone", Created.AddHours(1)));
    }

    [Fact]
    public void AReminderThatWasNeverAttemptedStartsWithoutAttemptsNorError()
    {
        var appointment = NewAppointment();

        Assert.Equal(0, appointment.ReminderAttempts);
        Assert.Null(appointment.ReminderClaimedUntil);
        Assert.Null(appointment.ReminderLastError);
        Assert.Null(appointment.ReminderSkippedReason);
    }

    private static Appointment ChannelAppointment() =>
        Appointment.Create(
            AppointmentId.New(), Tenant, LocationId.New(), ResourceId.New(), ServiceId.New(), Guid.CreateVersion7(), SessionId.New(),
            blockId: null, Start, Start.AddMinutes(30), AppointmentStatus.Confirmed, AppointmentSource.Channel, conversationId: null,
            note: null, actorMemberId: null, Created, byChannel: true);

    // D-17: sólo las escrituras del agente marcan su línea del historial como hechas por el canal.
    [Fact]
    public void ChannelWritesMarkTheirHistoryEntry()
    {
        var appointment = ChannelAppointment();
        appointment.Reschedule(
            ResourceId.New(), SessionId.New(), Start.AddDays(1), Start.AddDays(1).AddMinutes(30), null, Created.AddMinutes(1), byChannel: true);
        appointment.Cancel(null, null, Created.AddMinutes(2), byChannel: true);

        Assert.Equal(3, appointment.Events.Count);
        Assert.All(appointment.Events, entry => Assert.True(entry.ByChannel));
    }

    [Fact]
    public void WritesWithoutTheFlagAreNotByChannel()
    {
        var appointment = NewAppointment(AppointmentStatus.Pending);
        appointment.Confirm(Actor, Created.AddMinutes(1));
        appointment.Cancel(null, Actor, Created.AddMinutes(2));

        Assert.Equal(3, appointment.Events.Count);
        Assert.All(appointment.Events, entry => Assert.False(entry.ByChannel));
    }

    [Fact]
    public void ReminderEntriesAreNeverByChannel()
    {
        var appointment = ChannelAppointment();

        appointment.RecordReminderSent(Created.AddHours(1));

        Assert.Equal(AppointmentAction.ReminderSent, appointment.Events[^1].Action);
        Assert.False(appointment.Events[^1].ByChannel);
    }

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<SchedulingDomainException>(action);
        Assert.Equal(SchedulingErrorCodes.InvalidTransition, error.Code);
    }
}
