namespace Modules.Scheduling.Domain;

/// <summary>
/// Los valores con los que nace la configuración de un tenant (spec 2026-11-21, «Aprovisionamiento»). Un solo lugar, como
/// <c>PipelineSeed</c>. El recordatorio nace apagado porque necesita una plantilla aprobada por Meta que el tenant aún no tiene.
/// </summary>
public static class SchedulingDefaults
{
    public const ConfirmationMode ConfirmationMode = Domain.ConfirmationMode.Automatic;

    public const int MinNoticeMinutes = 120;

    public const int HorizonDays = 30;

    public const int ClientCancelCutoffHours = 24;

    public const bool ReminderEnabled = false;

    public const int ReminderOffsetHours = 24;
}
