using System.Diagnostics.CodeAnalysis;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-11-23 §7.1: quién llama al puerto. El tenant y el cliente salen del trabajo, nunca de un argumento del modelo (D-A30).</summary>
/// <param name="ConversationId"><c>null</c> en el playground.</param>
/// <param name="CustomerId">El cliente de la prueba; en un canal real el adaptador lo relee de la conversación (D-9).</param>
public sealed record AiSchedulingCaller(Guid TenantId, Guid AgentId, Guid JobId, Guid? ConversationId, Guid? CustomerId, bool IsTest)
{
    public static AiSchedulingCaller From(AgentToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new AiSchedulingCaller(
            invocation.TenantId, invocation.AgentId, invocation.JobId, invocation.ConversationId, invocation.CustomerId, invocation.IsTest);
    }
}

/// <summary>El resultado de un método del puerto: un valor, un código para el modelo, o un código más un traspaso (D-3, D-19).</summary>
/// <param name="HandoffCode">Si no es <c>null</c>, la herramienta termina el turno con este traspaso (<see cref="AgentHandoffCodes"/>).</param>
// CA1000: las fábricas estáticas son el contrato del puerto (spec §7.1) y leen mejor que un helper aparte.
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Result factories, spec 2026-11-23 §7.1.")]
public sealed record AiSchedulingResult<T>(T? Value, string? ErrorCode, string? HandoffCode)
    where T : class
{
    public static AiSchedulingResult<T> Ok(T value) => new(value, null, null);

    public static AiSchedulingResult<T> Error(string code) => new(null, code, null);

    public static AiSchedulingResult<T> HandedOff(string errorCode, string handoffCode) => new(null, errorCode, handoffCode);
}

/// <summary>Los instantes viajan como <see cref="DateTimeOffset"/> en la zona del negocio; la herramienta los escribe como hora local (D-10).</summary>
public sealed record AiBookableService(Guid ServiceId, string Name, int DurationMinutes);

public sealed record AiBookableServices(string TimeZone, IReadOnlyList<AiBookableService> Services, bool Truncated);

public sealed record AiSlotResource(Guid ResourceId, string Name);

public sealed record AiSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<AiSlotResource> Resources);

public sealed record AiAvailability(string TimeZone, Guid ServiceId, IReadOnlyList<AiSlot> Slots, bool Truncated);

/// <param name="CanChangeByChat">Lo calcula el backend con el plazo del tenant: el modelo no hace aritmética de plazos (§5.4).</param>
public sealed record AiAppointment(
    Guid AppointmentId,
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    Guid ServiceId,
    string ServiceName,
    string ResourceName,
    string LocationName,
    bool CanChangeByChat,
    bool CanRescheduleByChat,
    DateTimeOffset ChangeByChatUntil);

public sealed record AiAppointments(string TimeZone, IReadOnlyList<AiAppointment> Appointments, bool Truncated);

/// <param name="AlreadyDone">La llave (reservar) o el estado (cancelar, mover) ya estaban así: el resultado describe lo guardado (D-12, D-14).</param>
public sealed record AiAppointmentWrite(AiAppointment Appointment, bool AlreadyDone);

/// <summary>Lo que daría reservar ese inicio, sin escribir: para la herramienta simulada del playground (D-7).</summary>
public sealed record AiStartPreview(
    string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, string ServiceName, string ResourceName, string LocationName);

/// <summary>
/// Spec 2026-11-23 §7.1: la Agenda sin sesión para el agente (adaptador en Bootstrapper sobre <c>IChannelScheduling</c>). Ai no referencia
/// Scheduling, Mensajería ni Clientes: el adaptador traduce excepciones a códigos (§8) y resuelve el cliente de la conversación.
/// </summary>
public interface IAiScheduling
{
    Task<AgentSchedulingReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiBookableServices>> ListBookableServicesAsync(AiSchedulingCaller caller, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAvailability>> SearchAvailabilityAsync(
        AiSchedulingCaller caller, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointmentWrite>> BookAsync(
        AiSchedulingCaller caller,
        Guid serviceId,
        DateTime localStartsAt,
        Guid? resourceId,
        string? note,
        string clientRequestId,
        CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiStartPreview>> PreviewBookingAsync(
        AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller caller, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(
        AiSchedulingCaller caller, Guid appointmentId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(
        AiSchedulingCaller caller, Guid appointmentId, string? reason, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(
        AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);
}
