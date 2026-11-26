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

    /// <summary>
    /// Verdadero sólo cuando la línea la escribió el agente de IA por el canal (D-17). Con <see cref="ActorMemberId"/> nulo no alcanza para decirlo:
    /// el recordatorio y los cambios del sistema también lo dejan nulo. Las filas anteriores a la columna quedan en falso.
    /// </summary>
    public bool ByChannel { get; private set; }

    public static AppointmentEvent Create(
        AppointmentId appointmentId, AppointmentAction action, Guid? actorMemberId, string? detail, DateTimeOffset at,
        bool byChannel = false) =>
        new()
        {
            Id = AppointmentEventId.New(),
            AppointmentId = appointmentId,
            Action = action,
            At = at,
            ActorMemberId = actorMemberId,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            ByChannel = byChannel,
        };
}
