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

    /// <summary>El recordatorio no salió y no va a salir: el detalle dice por qué (sin teléfono, sin consentimiento, fallos agotados…).</summary>
    ReminderSkipped,
}
