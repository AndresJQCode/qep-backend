namespace Modules.Scheduling.Domain;

/// <summary>Lo que dibuja la tarjeta «Historial» de la cita (spec 2026-11-21, «Auditoría e historial»).</summary>
public enum AppointmentAction
{
    Created,
    Confirmed,
    Cancelled,
    Attended,
    NoShow,
    Rescheduled,
    ReminderSent,
}
