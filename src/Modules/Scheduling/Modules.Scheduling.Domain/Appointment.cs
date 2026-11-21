namespace Modules.Scheduling.Domain;

/// <summary>
/// La cita. Estados (spec 2026-11-21, «Citas»): <c>Pending → Confirmed | Cancelled</c>; <c>Confirmed → Cancelled | Completed | NoShow</c>;
/// los tres últimos son terminales. <c>Completed</c> y <c>NoShow</c> sólo después de la hora de inicio. Reprogramar conserva el id y el
/// estado. Cada transición deja una línea en el historial. El cupo de la sesión lo sube y baja Application junto con la cita.
/// </summary>
public sealed class Appointment : VersionedEntity
{
    private readonly List<AppointmentEvent> _events = [];

    private Appointment()
    {
    }

    public AppointmentId Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public ResourceId ResourceId { get; private set; }

    public ServiceId ServiceId { get; private set; }

    public Guid CustomerId { get; private set; }

    public SessionId SessionId { get; private set; }

    public AvailabilityBlockId? BlockId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public AppointmentStatus Status { get; private set; }

    public AppointmentSource Source { get; private set; }

    /// <summary>La conversación de Mensajería de la que salió, sin llave foránea: sólo para enlazar a ella.</summary>
    public Guid? ConversationId { get; private set; }

    public string? Note { get; private set; }

    public string? CancelReason { get; private set; }

    public DateTimeOffset? ReminderSentAt { get; private set; }

    public IReadOnlyList<AppointmentEvent> Events => _events;

    public static Appointment Create(
        AppointmentId id,
        Guid tenantId,
        LocationId locationId,
        ResourceId resourceId,
        ServiceId serviceId,
        Guid customerId,
        SessionId sessionId,
        AvailabilityBlockId? blockId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        AppointmentStatus initialStatus,
        AppointmentSource source,
        Guid? conversationId,
        string? note,
        Guid? actorMemberId,
        DateTimeOffset now)
    {
        if (initialStatus is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
        {
            throw new ArgumentException("An appointment is born Pending or Confirmed.", nameof(initialStatus));
        }

        RequireRange(startsAt, endsAt);

        var appointment = new Appointment
        {
            Id = id,
            LocationId = locationId,
            ResourceId = resourceId,
            ServiceId = serviceId,
            CustomerId = customerId,
            SessionId = sessionId,
            BlockId = blockId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = initialStatus,
            Source = source,
            ConversationId = conversationId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        appointment.Initialize(tenantId, now);
        appointment.Record(AppointmentAction.Created, actorMemberId, null, now, touch: false);
        return appointment;
    }

    public void Confirm(Guid? actorMemberId, DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Pending);
        Status = AppointmentStatus.Confirmed;
        Record(AppointmentAction.Confirmed, actorMemberId, null, now);
    }

    public void Cancel(string? reason, Guid? actorMemberId, DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Pending, AppointmentStatus.Confirmed);
        Status = AppointmentStatus.Cancelled;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Record(AppointmentAction.Cancelled, actorMemberId, CancelReason, now);
    }

    public void MarkAttended(Guid? actorMemberId, DateTimeOffset now)
    {
        RequireStarted(now);
        Status = AppointmentStatus.Completed;
        Record(AppointmentAction.Attended, actorMemberId, null, now);
    }

    public void MarkNoShow(Guid? actorMemberId, DateTimeOffset now)
    {
        RequireStarted(now);
        Status = AppointmentStatus.NoShow;
        Record(AppointmentAction.NoShow, actorMemberId, null, now);
    }

    /// <summary>Cambia de sesión (y con ella de recurso y de hora) conservando el id y el estado. Application ya validó la nueva sesión.</summary>
    public void Reschedule(
        ResourceId resourceId, SessionId sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid? actorMemberId, DateTimeOffset now)
    {
        RequireRange(startsAt, endsAt);
        RequireStatus(AppointmentStatus.Pending, AppointmentStatus.Confirmed);

        ResourceId = resourceId;
        SessionId = sessionId;
        StartsAt = startsAt;
        EndsAt = endsAt;
        Record(AppointmentAction.Rescheduled, actorMemberId, null, now);
    }

    /// <summary>El recordatorio sale una sola vez y sólo para una cita confirmada: si salió ya, es un error de quien reintenta.</summary>
    public void RecordReminderSent(DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Confirmed);
        if (ReminderSentAt is not null)
        {
            throw new InvalidOperationException("The reminder was already sent.");
        }

        ReminderSentAt = now;
        Record(AppointmentAction.ReminderSent, null, null, now);
    }

    private static void RequireRange(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("An appointment must end after it starts.", nameof(endsAt));
        }
    }

    private void RequireStatus(params AppointmentStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.InvalidTransition, $"An appointment in status '{Status}' cannot do that.");
        }
    }

    private void RequireStarted(DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Confirmed);
        if (now < StartsAt)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.BeforeStart, "The appointment cannot be marked before its start.");
        }
    }

    private void Record(AppointmentAction action, Guid? actorMemberId, string? detail, DateTimeOffset now, bool touch = true)
    {
        _events.Add(AppointmentEvent.Create(Id, action, actorMemberId, detail, now));
        if (touch)
        {
            Touch(now);
        }
    }
}
