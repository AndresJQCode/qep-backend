namespace Modules.Scheduling.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Spec 2026-11-21 (Agenda): cada `manage` exige también su `read`
// (como D-1 de RRHH). `agenda.read_all` no concede ninguna acción: sólo amplía sobre qué recursos valen las demás (D-7).
public static class SchedulingPermissions
{
    public const string AppointmentRead = "scheduling.appointment.read";

    public const string AppointmentManage = "scheduling.appointment.manage";

    public const string AvailabilityManage = "scheduling.availability.manage";

    public const string AgendaReadAll = "scheduling.agenda.read_all";

    public const string SettingsManage = "scheduling.settings.manage";
}
