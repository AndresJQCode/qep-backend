namespace Modules.Scheduling.Domain;

/// <summary>
/// Los códigos de dominio de la Agenda (plan 2026-11-21, «Contrato HTTP»). Sólo existen los que el contrato nombra: un código nuevo se
/// agrega a la tabla del plan primero. Todos llevan el prefijo <c>scheduling.</c>. El status lo decide el tipo de excepción, no el código:
/// <c>slot_taken</c> y <c>session_full</c> viajan en un <c>ResourceConflictException</c> (409) y el resto en un
/// <c>SchedulingDomainException</c> (422). Un recurso fuera de alcance no tiene código propio: es 403 <c>authorization.denied</c>.
/// </summary>
public static class SchedulingErrorCodes
{
    // 409
    public const string SlotTaken = "scheduling.appointment.slot_taken";
    public const string SessionFull = "scheduling.appointment.session_full";

    // 422
    public const string InvalidTransition = "scheduling.appointment.invalid_transition";
    public const string OutsideRules = "scheduling.appointment.outside_rules";
    public const string BeforeStart = "scheduling.appointment.before_start";
    public const string ClosureOverlapsAppointments = "scheduling.closure.overlaps_appointments";
    public const string ResourceHasFutureAppointments = "scheduling.resource.has_future_appointments";
    public const string ServiceInUse = "scheduling.service.in_use";
    public const string MainLocationCannotBeArchived = "scheduling.location.main_cannot_be_archived";
    public const string ResourceMemberAlreadyLinked = "scheduling.resource.member_already_linked";
    // Spec 2026-11-23, D-16: la franja no deja al cliente elegir el recurso; sólo lo aplica el camino del agente.
    public const string ResourceNotChoosable = "scheduling.appointment.resource_not_choosable";
    public const string SessionNotPublishedMode = "scheduling.session.not_published_mode";

    // Tarea 8: una sesión con citas (aun canceladas) no se quita, porque su historial le apunta.
    public const string SessionHasAppointments = "scheduling.session.has_appointments";
    public const string ReminderConnectionInvalid = "scheduling.reminder.connection_invalid";

    // 404: lo que se pide por id dentro del tenant del llamador y no está entre sus datos. Uno de otro tenant no llega acá: lo frena el 403.
    public const string LocationNotFound = "scheduling.location.not_found";
    public const string ResourceNotFound = "scheduling.resource.not_found";
    public const string ServiceNotFound = "scheduling.service.not_found";
    public const string BlockNotFound = "scheduling.block.not_found";
    public const string ClosureNotFound = "scheduling.closure.not_found";
    public const string SessionNotFound = "scheduling.session.not_found";

    // Tarea 9: toda entidad direccionable por id tiene su 404; la cita no estaba en el contrato.
    public const string AppointmentNotFound = "scheduling.appointment.not_found";
}
