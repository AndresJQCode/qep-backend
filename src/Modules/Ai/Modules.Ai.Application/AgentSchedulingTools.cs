using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
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

/// <summary>Lo que daría mover esa cita, sin escribir: la herramienta simulada del playground (D-7).</summary>
public sealed record AiReschedulePreview(AiStartPreview Start, bool AlreadyDone);

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

    Task<AiSchedulingResult<AiReschedulePreview>> PreviewRescheduleAsync(
        AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller caller, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(
        AiSchedulingCaller caller, Guid appointmentId, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(
        AiSchedulingCaller caller, Guid appointmentId, string? reason, CancellationToken cancellationToken);

    Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(
        AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-23 D-10: las horas viajan en hora local del negocio, <c>yyyy-MM-ddTHH:mm</c>, sin desfase y con una sola <c>timeZone</c> por
/// resultado. Cultura invariante y formato exacto: el modelo copia lo que lee, y una hora con otra forma es <c>invalid_arguments</c>.
/// </summary>
public static class AgentSchedulingFormat
{
    public const string LocalPattern = "yyyy-MM-dd'T'HH:mm";
    public const string DayPattern = "yyyy-MM-dd";

    /// <summary>Los campos de reloj del instante tal como vienen: Scheduling ya los entrega en la zona del negocio.</summary>
    public static string Local(DateTimeOffset value) => value.ToString(LocalPattern, CultureInfo.InvariantCulture);

    public static bool TryParseLocal(string? text, out DateTime value)
    {
        value = default;
        if (text is null || !DateTime.TryParseExact(text, LocalPattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    public static bool TryParseDay(string? text, out DateOnly value)
    {
        value = default;
        return text is not null && DateOnly.TryParseExact(text, DayPattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}

/// <summary>Lectura común de los argumentos de la Agenda. Una propiedad ausente de un campo que admite <c>null</c> se lee como <c>null</c>.</summary>
internal static class AgentSchedulingArguments
{
    public const int NoteMaxLength = 500;

    public static bool TryOptionalGuid(JsonElement arguments, string name, out Guid? value)
    {
        value = null;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return arguments.ValueKind == JsonValueKind.Object;
        }

        if (property.ValueKind != JsonValueKind.String || !Guid.TryParse(property.GetString(), out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Texto libre que admite <c>null</c>: recortado, vacío es ninguno y pasado el tope es inválido.</summary>
    public static bool TryOptionalText(JsonElement arguments, string name, out string? value)
    {
        value = null;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return arguments.ValueKind == JsonValueKind.Object;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString()!.Trim();
        if (text.Length > NoteMaxLength)
        {
            return false;
        }

        value = text.Length == 0 ? null : text;
        return true;
    }

    public static bool TryGuid(JsonElement arguments, string name, out Guid value)
    {
        value = default;
        return AgentToolArguments.TryString(arguments, name, out var text) && Guid.TryParse(text, out value);
    }

    public static bool TryLocal(JsonElement arguments, string name, out DateTime value)
    {
        value = default;
        return AgentToolArguments.TryString(arguments, name, out var text) && AgentSchedulingFormat.TryParseLocal(text, out value);
    }

    public static bool TryAppointmentId(JsonElement arguments, out string appointmentId)
    {
        appointmentId = string.Empty;
        if (!AgentToolArguments.TryString(arguments, "appointmentId", out var text) || text.Trim().Length is 0 or > 64)
        {
            return false;
        }

        appointmentId = text.Trim();
        return true;
    }

    /// <summary>Del puerto al modelo (§8): el código vuelve como error y, con traspaso, termina el turno (D-3, D-19).</summary>
    public static AgentToolResult Map<T>(AiSchedulingResult<T> result, Func<T, object> project)
        where T : class
    {
        if (result.ErrorCode is { } code)
        {
            var error = AgentToolResult.Error(code);
            return result.HandoffCode is { } handoff ? error with { Handoff = new AgentToolHandoff(handoff, null) } : error;
        }

        return result.Value is { } value ? AgentToolResult.Ok(project(value)) : AgentToolResult.Error(AgentToolErrors.ToolFailed);
    }

    public static AgentToolResult Blocked() =>
        AgentToolResult.Error(AgentToolErrors.AppointmentChangeBlocked) with
        {
            Handoff = new AgentToolHandoff(AgentHandoffCodes.AppointmentChangeBlocked, null),
        };

    public static object Describe(AiAppointment appointment, bool already, string keyName) =>
        new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.AppointmentId,
            ["status"] = appointment.Status,
            ["startsAt"] = AgentSchedulingFormat.Local(appointment.StartsAt),
            ["endsAt"] = AgentSchedulingFormat.Local(appointment.EndsAt),
            ["timeZone"] = appointment.TimeZone,
            ["serviceName"] = appointment.ServiceName,
            ["resourceName"] = appointment.ResourceName,
            ["locationName"] = appointment.LocationName,
            [keyName] = already,
        };

    public static bool IsOpen(AiAppointment appointment) => appointment.Status is "Pending" or "Confirmed";
}

/// <summary>§5.1: <c>list_bookable_services</c>. Sólo lectura, sin argumentos.</summary>
public sealed class ListBookableServicesTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        AgentToolNames.ListBookableServices,
        "Lista los servicios de la agenda del negocio que el cliente puede reservar por este chat, con su duración.",
        """{"type":"object","properties":{},"required":[],"additionalProperties":false}""");

    public string Capability => AiServicePrincipal.SchedulingRead;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        var result = await scheduling.ListBookableServicesAsync(AiSchedulingCaller.From(invocation), cancellationToken);
        return AgentSchedulingArguments.Map(result, list => new
        {
            timeZone = list.TimeZone,
            services = list.Services.Select(service => new { serviceId = service.ServiceId, name = service.Name, durationMinutes = service.DurationMinutes }),
            truncated = list.Truncated,
        });
    }
}

/// <summary>§5.2: el contrato de <c>check_availability</c>.</summary>
public static class CheckAvailabilityArguments
{
    /// <summary>Máximo de días del rango, contando los dos extremos.</summary>
    public const int MaxDays = 14;

    public static readonly AiToolDefinition Definition = new(
        AgentToolNames.CheckAvailability,
        "Consulta los horarios libres de un servicio entre dos días (hora local del negocio). Ofrece al cliente sólo horarios que devuelva esta herramienta.",
        """
        {"type":"object","properties":{"serviceId":{"type":"string","description":"El serviceId que devolvió list_bookable_services."},"fromDate":{"type":"string","description":"Primer día, yyyy-MM-dd, en la fecha del negocio."},"toDate":{"type":"string","description":"Último día, yyyy-MM-dd, inclusive. Máximo 14 días desde fromDate."},"resourceId":{"type":["string","null"],"description":"Con quién, si el cliente eligió uno de los que devolvió esta herramienta; null para cualquiera."}},"required":["serviceId","fromDate","toDate","resourceId"],"additionalProperties":false}
        """);

    public static bool TryRead(JsonElement arguments, out Guid serviceId, out DateOnly from, out DateOnly to, out Guid? resourceId)
    {
        from = default;
        to = default;
        resourceId = null;
        serviceId = default;
        return AgentSchedulingArguments.TryGuid(arguments, "serviceId", out serviceId)
            && AgentToolArguments.TryString(arguments, "fromDate", out var fromText)
            && AgentSchedulingFormat.TryParseDay(fromText, out from)
            && AgentToolArguments.TryString(arguments, "toDate", out var toText)
            && AgentSchedulingFormat.TryParseDay(toText, out to)
            && to >= from
            && to.DayNumber - from.DayNumber < MaxDays
            && AgentSchedulingArguments.TryOptionalGuid(arguments, "resourceId", out resourceId);
    }
}

/// <summary>§5.2: <c>check_availability</c>. Hasta 30 inicios, los más próximos primero.</summary>
public sealed class CheckAvailabilityTool(IAiScheduling scheduling) : IAgentTool
{
    public const int MaxSlots = 30;

    public AiToolDefinition Definition => CheckAvailabilityArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingRead;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!CheckAvailabilityArguments.TryRead(arguments, out var serviceId, out var from, out var to, out var resourceId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var result = await scheduling.SearchAvailabilityAsync(AiSchedulingCaller.From(invocation), serviceId, from, to, resourceId, cancellationToken);
        return AgentSchedulingArguments.Map(result, availability => new
        {
            timeZone = availability.TimeZone,
            serviceId = availability.ServiceId,
            slots = availability.Slots.Take(MaxSlots).Select(slot => new
            {
                startsAt = AgentSchedulingFormat.Local(slot.StartsAt),
                endsAt = AgentSchedulingFormat.Local(slot.EndsAt),
                seatsLeft = slot.SeatsLeft,
                resources = slot.Resources.Select(resource => new { resourceId = resource.ResourceId, name = resource.Name }),
            }),
            truncated = availability.Truncated || availability.Slots.Count > MaxSlots,
        });
    }
}

/// <summary>§5.3: el contrato de <c>book_appointment</c>, el mismo para la real y la simulada.</summary>
public static class BookAppointmentArguments
{
    public const int NoteMaxLength = AgentSchedulingArguments.NoteMaxLength;

    public static readonly AiToolDefinition Definition = new(
        AgentToolNames.BookAppointment,
        "Reserva una cita para el cliente de esta conversación en un horario que devolvió check_availability. Confirma antes con el cliente el servicio, el día y la hora.",
        """
        {"type":"object","properties":{"serviceId":{"type":"string","description":"El serviceId del servicio."},"startsAt":{"type":"string","description":"Inicio, yyyy-MM-ddTHH:mm en hora local del negocio, tal como lo devolvió check_availability."},"resourceId":{"type":["string","null"],"description":"Con quién, sólo si el cliente eligió uno de los que ofreció check_availability; null para el primero libre."},"note":{"type":["string","null"],"description":"Nota para el equipo, máximo 500 caracteres. Nunca datos sensibles."}},"required":["serviceId","startsAt","resourceId","note"],"additionalProperties":false}
        """);

    public static bool TryRead(JsonElement arguments, out Guid serviceId, out DateTime localStartsAt, out Guid? resourceId, out string? note)
    {
        localStartsAt = default;
        resourceId = null;
        note = null;
        return AgentSchedulingArguments.TryGuid(arguments, "serviceId", out serviceId)
            && AgentSchedulingArguments.TryLocal(arguments, "startsAt", out localStartsAt)
            && AgentSchedulingArguments.TryOptionalGuid(arguments, "resourceId", out resourceId)
            && AgentSchedulingArguments.TryOptionalText(arguments, "note", out note);
    }
}

/// <summary>
/// §5.3 y D-14: <c>book_appointment</c> en un canal real. La llave de idempotencia es del trabajo y del ordinal de la reserva dentro de él,
/// nunca del id de la llamada, que el modelo inventa y cambia si el trabajo se retoma.
/// </summary>
public sealed class BookAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => BookAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingBookOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => !isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!BookAppointmentArguments.TryRead(arguments, out var serviceId, out var startsAt, out var resourceId, out var note))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var result = await scheduling.BookAsync(
            AiSchedulingCaller.From(invocation), serviceId, startsAt, resourceId, note, ClientRequestId(invocation), cancellationToken);
        return AgentSchedulingArguments.Map(
            result, write => AgentSchedulingArguments.Describe(write.Appointment, write.AlreadyDone, "alreadyBooked"));
    }

    /// <summary>D-14: <c>ai:{jobId:N}:book:{n}</c>. Un trabajo retomado vuelve a contar desde 1 y encuentra su cita; una segunda cita del
    /// mismo turno lleva 2; una llamada con error no consume número.</summary>
    internal static string ClientRequestId(AgentToolInvocation invocation) => $"ai:{invocation.JobId:N}:book:{invocation.BookingOrdinal}";
}

/// <summary>§7.7: <c>book_appointment</c> sólo en el playground. Valida y lee lo que reservaría la real, sin escribir ni crear cliente.</summary>
public sealed class SimulatedBookAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => BookAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingBookOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!BookAppointmentArguments.TryRead(arguments, out var serviceId, out var startsAt, out var resourceId, out _))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var result = await scheduling.PreviewBookingAsync(AiSchedulingCaller.From(invocation), serviceId, startsAt, resourceId, cancellationToken);
        var id = $"PRUEBA-{invocation.JobId.ToString("N")[..8].ToUpperInvariant()}";
        return AgentSchedulingArguments.Map(result, preview => new Dictionary<string, object?>
        {
            ["appointmentId"] = id,
            ["status"] = preview.Status,
            ["startsAt"] = AgentSchedulingFormat.Local(preview.StartsAt),
            ["endsAt"] = AgentSchedulingFormat.Local(preview.EndsAt),
            ["timeZone"] = preview.TimeZone,
            ["serviceName"] = preview.ServiceName,
            ["resourceName"] = preview.ResourceName,
            ["locationName"] = preview.LocationName,
            ["alreadyBooked"] = false,
        });
    }
}

/// <summary>§5.4: <c>list_my_appointments</c>. Hasta 10 citas abiertas del cliente de la conversación.</summary>
public sealed class ListMyAppointmentsTool(IAiScheduling scheduling) : IAgentTool
{
    public const int MaxAppointments = 10;

    public AiToolDefinition Definition { get; } = new(
        AgentToolNames.ListMyAppointments,
        "Lista las próximas citas del cliente de esta conversación (pendientes y confirmadas).",
        """{"type":"object","properties":{},"required":[],"additionalProperties":false}""");

    public string Capability => AiServicePrincipal.SchedulingReadOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        var result = await scheduling.ListMyAppointmentsAsync(AiSchedulingCaller.From(invocation), cancellationToken);
        return AgentSchedulingArguments.Map(result, list => new
        {
            timeZone = list.TimeZone,
            appointments = list.Appointments.Take(MaxAppointments).Select(appointment => new
            {
                appointmentId = appointment.AppointmentId,
                status = appointment.Status,
                startsAt = AgentSchedulingFormat.Local(appointment.StartsAt),
                endsAt = AgentSchedulingFormat.Local(appointment.EndsAt),
                serviceId = appointment.ServiceId,
                serviceName = appointment.ServiceName,
                resourceName = appointment.ResourceName,
                locationName = appointment.LocationName,
                canChangeByChat = appointment.CanChangeByChat,
                canRescheduleByChat = appointment.CanRescheduleByChat,
                changeByChatUntil = AgentSchedulingFormat.Local(appointment.ChangeByChatUntil),
            }),
            truncated = list.Truncated || list.Appointments.Count > MaxAppointments,
        });
    }
}

/// <summary>§5.5: el contrato de <c>cancel_appointment</c>. El id viaja como texto para que la simulada reconozca <c>PRUEBA-…</c>.</summary>
public static class CancelAppointmentArguments
{
    public const int ReasonMaxLength = 500;

    public static readonly AiToolDefinition Definition = new(
        AgentToolNames.CancelAppointment,
        "Cancela una cita del cliente de esta conversación. Confirma antes con el cliente cuál cita.",
        """
        {"type":"object","properties":{"appointmentId":{"type":"string","description":"El appointmentId que devolvió list_my_appointments."},"reason":{"type":["string","null"],"description":"Motivo que dio el cliente, máximo 500 caracteres; null si no dio ninguno."}},"required":["appointmentId","reason"],"additionalProperties":false}
        """);

    public static bool TryRead(JsonElement arguments, out string appointmentId, out string? reason)
    {
        reason = null;
        return AgentSchedulingArguments.TryAppointmentId(arguments, out appointmentId)
            && AgentSchedulingArguments.TryOptionalText(arguments, "reason", out reason);
    }
}

/// <summary>§5.5, D-12 y D-3: <c>cancel_appointment</c> en un canal real. Repetirla es éxito (<c>alreadyDone</c>); el plazo vencido o una
/// cita cerrada traspasan.</summary>
public sealed class CancelAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => CancelAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingChangeOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => !isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!CancelAppointmentArguments.TryRead(arguments, out var id, out var reason) || !Guid.TryParse(id, out var appointmentId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var result = await scheduling.CancelAsync(AiSchedulingCaller.From(invocation), appointmentId, reason, cancellationToken);
        return AgentSchedulingArguments.Map(
            result, write => new { appointmentId = write.Appointment.AppointmentId, status = write.Appointment.Status, alreadyDone = write.AlreadyDone });
    }
}

/// <summary>§7.7: <c>cancel_appointment</c> sólo en el playground. Un id <c>PRUEBA-…</c> se da por encontrado; uno real tiene que ser del
/// cliente de la prueba y el plazo se evalúa con una lectura. No escribe.</summary>
public sealed class SimulatedCancelAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => CancelAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingChangeOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!CancelAppointmentArguments.TryRead(arguments, out var id, out _))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        if (id.StartsWith(SimulatedSchedulingIds.Prefix, StringComparison.Ordinal))
        {
            return AgentToolResult.Ok(new { appointmentId = id, status = "Cancelled", alreadyDone = false });
        }

        if (!Guid.TryParse(id, out var appointmentId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var found = await scheduling.FindMyAppointmentAsync(AiSchedulingCaller.From(invocation), appointmentId, cancellationToken);
        if (found.Value is not { } appointment)
        {
            return AgentSchedulingArguments.Map(found, _ => new { });
        }

        if (appointment.Status == "Cancelled")
        {
            return AgentToolResult.Ok(new { appointmentId = appointment.AppointmentId, status = "Cancelled", alreadyDone = true });
        }

        return !AgentSchedulingArguments.IsOpen(appointment) || !appointment.CanChangeByChat
            ? AgentSchedulingArguments.Blocked()
            : AgentToolResult.Ok(new { appointmentId = appointment.AppointmentId, status = "Cancelled", alreadyDone = false });
    }
}

/// <summary>§5.6: el contrato de <c>reschedule_appointment</c>.</summary>
public static class RescheduleAppointmentArguments
{
    public static readonly AiToolDefinition Definition = new(
        AgentToolNames.RescheduleAppointment,
        "Cambia la hora de una cita del cliente de esta conversación a un horario que devolvió check_availability para el mismo servicio.",
        """
        {"type":"object","properties":{"appointmentId":{"type":"string","description":"El appointmentId que devolvió list_my_appointments."},"startsAt":{"type":"string","description":"Nuevo inicio, yyyy-MM-ddTHH:mm en hora local del negocio, tal como lo devolvió check_availability."},"resourceId":{"type":["string","null"],"description":"Con quién, sólo si el cliente eligió uno de los que ofreció check_availability; null para el primero libre."}},"required":["appointmentId","startsAt","resourceId"],"additionalProperties":false}
        """);

    public static bool TryRead(JsonElement arguments, out string appointmentId, out DateTime localStartsAt, out Guid? resourceId)
    {
        localStartsAt = default;
        resourceId = null;
        return AgentSchedulingArguments.TryAppointmentId(arguments, out appointmentId)
            && AgentSchedulingArguments.TryLocal(arguments, "startsAt", out localStartsAt)
            && AgentSchedulingArguments.TryOptionalGuid(arguments, "resourceId", out resourceId);
    }
}

/// <summary>§5.6, D-12 y D-15: <c>reschedule_appointment</c> en un canal real. Cambiar a la hora que ya tiene es éxito; el plazo vencido, una
/// cita cerrada o un servicio sin reserva por chat traspasan.</summary>
public sealed class RescheduleAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => RescheduleAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingChangeOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => !isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!RescheduleAppointmentArguments.TryRead(arguments, out var id, out var startsAt, out var resourceId)
            || !Guid.TryParse(id, out var appointmentId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var result = await scheduling.RescheduleAsync(AiSchedulingCaller.From(invocation), appointmentId, startsAt, resourceId, cancellationToken);
        return AgentSchedulingArguments.Map(
            result, write => AgentSchedulingArguments.Describe(write.Appointment, write.AlreadyDone, "alreadyDone"));
    }
}

/// <summary>§7.7: <c>reschedule_appointment</c> sólo en el playground. Un id <c>PRUEBA-…</c> se da por encontrado y abierto, y no se valida el
/// inicio: la cita simulada no guarda su servicio (decisión del plan; el spec sólo dice que se da por encontrada). Uno real se lee y su
/// inicio nuevo se comprueba como lo haría el cambio de hora real (misma sede, sin contar el cupo de la propia cita), no como una reserva.</summary>
public sealed class SimulatedRescheduleAppointmentTool(IAiScheduling scheduling) : IAgentTool
{
    public AiToolDefinition Definition => RescheduleAppointmentArguments.Definition;

    public string Capability => AiServicePrincipal.SchedulingChangeOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => isTest && tools.Scheduling;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!RescheduleAppointmentArguments.TryRead(arguments, out var id, out var startsAt, out var resourceId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        if (id.StartsWith(SimulatedSchedulingIds.Prefix, StringComparison.Ordinal))
        {
            return AgentToolResult.Ok(new
            {
                appointmentId = id,
                status = "Pending",
                startsAt = startsAt.ToString(AgentSchedulingFormat.LocalPattern, CultureInfo.InvariantCulture),
                alreadyDone = false,
            });
        }

        if (!Guid.TryParse(id, out var appointmentId))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var caller = AiSchedulingCaller.From(invocation);
        var found = await scheduling.FindMyAppointmentAsync(caller, appointmentId, cancellationToken);
        if (found.Value is not { } appointment)
        {
            return AgentSchedulingArguments.Map(found, _ => new { });
        }

        if (!AgentSchedulingArguments.IsOpen(appointment) || !appointment.CanRescheduleByChat)
        {
            return AgentSchedulingArguments.Blocked();
        }

        var preview = await scheduling.PreviewRescheduleAsync(caller, appointment.AppointmentId, startsAt, resourceId, cancellationToken);
        return AgentSchedulingArguments.Map(preview, value => new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.AppointmentId,
            ["status"] = appointment.Status,
            ["startsAt"] = AgentSchedulingFormat.Local(value.Start.StartsAt),
            ["endsAt"] = AgentSchedulingFormat.Local(value.Start.EndsAt),
            ["timeZone"] = value.Start.TimeZone,
            ["serviceName"] = value.Start.ServiceName,
            ["resourceName"] = value.Start.ResourceName,
            ["locationName"] = value.Start.LocationName,
            ["alreadyDone"] = value.AlreadyDone,
        });
    }
}

internal static class SimulatedSchedulingIds
{
    /// <summary>El prefijo de los ids ficticios que devuelve la reserva simulada (como el borrador de cotización).</summary>
    public const string Prefix = "PRUEBA-";
}
