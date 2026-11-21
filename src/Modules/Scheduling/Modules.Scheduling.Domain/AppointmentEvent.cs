namespace Modules.Scheduling.Domain;

/// <summary>Una línea del historial de la cita. <see cref="ActorMemberId"/> es nulo cuando lo hizo el sistema o el cliente por un canal.</summary>
public sealed class AppointmentEvent
{
    private AppointmentEvent()
    {
    }

    public AppointmentEventId Id { get; private set; }

    public AppointmentId AppointmentId { get; private set; }

    public AppointmentAction Action { get; private set; }

    public DateTimeOffset At { get; private set; }

    public Guid? ActorMemberId { get; private set; }

    public string? Detail { get; private set; }

    public static AppointmentEvent Create(
        AppointmentId appointmentId, AppointmentAction action, Guid? actorMemberId, string? detail, DateTimeOffset at) =>
        new()
        {
            Id = AppointmentEventId.New(),
            AppointmentId = appointmentId,
            Action = action,
            At = at,
            ActorMemberId = actorMemberId,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
        };
}
