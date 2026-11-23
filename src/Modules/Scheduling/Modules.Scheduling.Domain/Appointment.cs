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

    /// <summary>How many times the reminder worker claimed this appointment to send its reminder. The worker claims with a single UPDATE so two
    /// pods never send the same one; this only mirrors that column.</summary>
    public int ReminderAttempts { get; private set; }

    /// <summary>Until when the last claim of the reminder worker holds: a failed send is retried only after it.</summary>
    public DateTimeOffset? ReminderClaimedUntil { get; private set; }

    /// <summary>The code of the last failed send of the reminder, kept for whoever has to look at it.</summary>
    public string? ReminderLastError { get; private set; }

    /// <summary>Why the reminder will never be sent (no phone, revoked consent, retries exhausted…). Final: it is also in the history.</summary>
    public string? ReminderSkippedReason { get; private set; }

    public const int ReminderReasonMaxLength = 150;

    /// <summary>
    /// The key the caller (the AI agent, a retrying client) booked with: a repeat of the same key answers with this appointment instead of
    /// taking a second seat. Unique per tenant when present.
    /// </summary>
    public string? ClientRequestId { get; private set; }

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
        DateTimeOffset now,
        string? clientRequestId = null,
        bool byChannel = false)
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
            ClientRequestId = string.IsNullOrWhiteSpace(clientRequestId) ? null : clientRequestId.Trim(),
        };
        appointment.Initialize(tenantId, now);
        appointment.Record(AppointmentAction.Created, actorMemberId, null, now, touch: false, byChannel: byChannel);
        return appointment;
    }

    public void Confirm(Guid? actorMemberId, DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Pending);
        Status = AppointmentStatus.Confirmed;
        Record(AppointmentAction.Confirmed, actorMemberId, null, now);
    }

    public void Cancel(string? reason, Guid? actorMemberId, DateTimeOffset now, bool byChannel = false)
    {
        RequireStatus(AppointmentStatus.Pending, AppointmentStatus.Confirmed);
        Status = AppointmentStatus.Cancelled;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Record(AppointmentAction.Cancelled, actorMemberId, CancelReason, now, byChannel: byChannel);
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

    /// <summary>
    /// Cambia de sesión (y con ella de recurso y de hora) conservando el id y el estado. Application ya validó la nueva sesión. Limpia todo el
    /// estado del recordatorio (enviado, omitido, intentos, reclamo y último error): la nueva hora se avisa de nuevo (plan, corrección 49).
    /// </summary>
    public void Reschedule(
        ResourceId resourceId, SessionId sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid? actorMemberId, DateTimeOffset now,
        bool byChannel = false)
    {
        RequireRange(startsAt, endsAt);
        RequireStatus(AppointmentStatus.Pending, AppointmentStatus.Confirmed);

        ResourceId = resourceId;
        SessionId = sessionId;
        StartsAt = startsAt;
        EndsAt = endsAt;
        ReminderSentAt = null;
        ReminderSkippedReason = null;
        ReminderAttempts = 0;
        ReminderClaimedUntil = null;
        ReminderLastError = null;
        Record(AppointmentAction.Rescheduled, actorMemberId, null, now, byChannel: byChannel);
    }

    /// <summary>
    /// El recordatorio sale una sola vez y sólo para una cita confirmada: si salió ya (o se omitió), es un error de quien reintenta. Es una
    /// escritura del sistema y no sube <see cref="VersionedEntity.Version"/> (<c>touch: false</c>): una pestaña abierta con la cita no debe
    /// recibir un 412 porque el barredor anotó el aviso.
    /// </summary>
    public void RecordReminderSent(DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Confirmed);
        RequireReminderPending();
        ReminderSentAt = now;
        ReminderLastError = null;
        Record(AppointmentAction.ReminderSent, null, null, now, touch: false);
    }

    /// <summary>
    /// The reminder will not go out: the reason stays on the row and in the history, so nobody has to guess why a client was not reminded.
    /// Final, like <see cref="RecordReminderSent"/>: the worker does not evaluate this appointment again.
    /// </summary>
    public void RecordReminderSkipped(string reason, DateTimeOffset now)
    {
        RequireStatus(AppointmentStatus.Confirmed);
        RequireReminderPending();
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > ReminderReasonMaxLength)
        {
            throw new ArgumentException($"The reason must have between 1 and {ReminderReasonMaxLength} characters.", nameof(reason));
        }

        ReminderSkippedReason = reason.Trim();
        Record(AppointmentAction.ReminderSkipped, null, ReminderSkippedReason, now, touch: false);
    }

    private void RequireReminderPending()
    {
        if (ReminderSentAt is not null)
        {
            throw new InvalidOperationException("The reminder was already sent.");
        }

        if (ReminderSkippedReason is not null)
        {
            throw new InvalidOperationException("The reminder was already skipped.");
        }
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

    private void Record(
        AppointmentAction action, Guid? actorMemberId, string? detail, DateTimeOffset now, bool touch = true, bool byChannel = false)
    {
        _events.Add(AppointmentEvent.Create(Id, action, actorMemberId, detail, now, byChannel));
        if (touch)
        {
            Touch(now);
        }
    }
}
