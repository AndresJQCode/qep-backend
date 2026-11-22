namespace Modules.Scheduling.Application;

/// <summary>
/// Acciones de <c>audit.entries</c> de la Agenda. Guardan <b>nombres</b> de campos (camelCase), nunca valores. Las de las citas llegaron con la
/// Tarea 9 del plan (la auditoría de configuración se había adelantado a la Tarea 7).
/// </summary>
public static class SchedulingAuditActions
{
    public const string LocationCreated = "scheduling.location.created";
    public const string LocationUpdated = "scheduling.location.updated";
    public const string LocationArchived = "scheduling.location.archived";

    public const string ResourceCreated = "scheduling.resource.created";
    public const string ResourceUpdated = "scheduling.resource.updated";
    public const string ResourceArchived = "scheduling.resource.archived";

    public const string ServiceCreated = "scheduling.service.created";
    public const string ServiceUpdated = "scheduling.service.updated";
    public const string ServiceArchived = "scheduling.service.archived";

    public const string SettingsUpdated = "scheduling.settings.updated";

    public const string BlockCreated = "scheduling.block.created";
    public const string BlockUpdated = "scheduling.block.updated";
    public const string BlockArchived = "scheduling.block.archived";

    public const string ClosureCreated = "scheduling.closure.created";
    public const string ClosureDeleted = "scheduling.closure.deleted";

    public const string SessionPublished = "scheduling.session.published";
    public const string SessionRemoved = "scheduling.session.removed";
    public const string SessionDeactivated = "scheduling.session.deactivated";
    public const string SessionReactivated = "scheduling.session.reactivated";

    public const string AppointmentCreated = "scheduling.appointment.created";
    public const string AppointmentConfirmed = "scheduling.appointment.confirmed";
    public const string AppointmentCancelled = "scheduling.appointment.cancelled";
    public const string AppointmentAttended = "scheduling.appointment.attended";
    public const string AppointmentNoShow = "scheduling.appointment.no_show";
    public const string AppointmentRescheduled = "scheduling.appointment.rescheduled";

    public static class Resources
    {
        public const string Location = "location";
        public const string Resource = "resource";
        public const string Service = "service";
        public const string Settings = "settings";
        public const string Block = "block";
        public const string Closure = "closure";
        public const string Session = "session";
        public const string Appointment = "appointment";
    }
}
