namespace Modules.Scheduling.Application;

// Todo es anulable para que el validador devuelva el campo y no un 400 de binding.

/// <summary>El cuerpo de <c>POST</c> y <c>PUT locations</c>. Una sede nueva nunca nace principal: no hay campo para pedirlo.</summary>
public sealed record LocationRequest(string? Name);

/// <param name="LocationId">Al crear, la sede del recurso; sin ella, la principal. En el <c>PUT</c> es la guardada: la sede de un recurso no cambia.</param>
/// <param name="MemberId">El miembro vinculado, opcional: define qué agenda ve quien no tiene «ver todas las agendas».</param>
public sealed record ResourceRequest(Guid? LocationId, string? DisplayName, Guid? MemberId);

/// <param name="BufferMinutes">Nulo es 0.</param>
/// <param name="BookableByChannel">Nulo es <c>false</c>.</param>
public sealed record ServiceRequest(string? Name, int? DurationMinutes, int? BufferMinutes, bool? BookableByChannel);

/// <param name="ConfirmationMode">El nombre del miembro de <c>ConfirmationMode</c>: <c>Automatic</c> o <c>Manual</c>.</param>
/// <param name="ReminderEnabled">Nulo es <c>false</c>.</param>
/// <summary>Minutos locales del día, <c>[From, To)</c>, entre 0 y 1440.</summary>
public sealed record TimeRangeRequest(int? From, int? To);

/// <param name="Day">De 1 (lunes) a 7 (domingo).</param>
public sealed record WeekdayHoursRequest(int? Day, IReadOnlyList<TimeRangeRequest>? Ranges);

public sealed record DateHoursRequest(DateOnly? Date, IReadOnlyList<TimeRangeRequest>? Ranges);

/// <summary>
/// El cuerpo de <c>POST</c> y <c>PUT blocks</c>. La sede no se pide: sale de los recursos, que deben ser de una misma sede.
/// </summary>
/// <param name="Kind">El nombre del miembro de <c>BlockKind</c>: <c>Weekly</c> (con <paramref name="Weekly"/>) u <c>OneOff</c> (con <paramref name="OneOff"/>).</param>
/// <param name="ClosesOnHolidays">Nulo es <c>false</c>.</param>
/// <param name="CapacityPerResource">Nulo es 1.</param>
/// <param name="SessionMode">Sólo cuenta con cupo mayor que 1; nulo es <c>FirstBookingOpens</c>.</param>
/// <param name="AllowClientChoice">Nulo es <c>false</c>.</param>
/// <param name="AssignmentRule">Nulo es <c>FirstFree</c>, la única hoy.</param>
/// <param name="ResourceIds">En orden: es el de la asignación «primero libre».</param>
public sealed record BlockRequest(
    string? Name,
    string? Kind,
    IReadOnlyList<WeekdayHoursRequest>? Weekly,
    DateHoursRequest? OneOff,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool? ClosesOnHolidays,
    int? CapacityPerResource,
    string? SessionMode,
    bool? AllowClientChoice,
    string? AssignmentRule,
    IReadOnlyList<Guid>? ServiceIds,
    IReadOnlyList<Guid>? ResourceIds);

/// <param name="LocationId">Sin recurso, la sede que se cierra (sin ella, la principal). Con recurso, es la de ese recurso.</param>
/// <param name="ResourceId">Nulo cierra la sede entera.</param>
public sealed record ClosureRequest(Guid? LocationId, Guid? ResourceId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Reason);

/// <summary>Publicar una sesión de una franja <c>PublishedSessions</c>: el recurso, el servicio y el inicio, que debe caer en la ventana de la franja.</summary>
public sealed record PublishSessionRequest(Guid? BlockId, Guid? ResourceId, Guid? ServiceId, DateTimeOffset? StartsAt);

/// <summary>
/// The whole rules form: a <c>PUT</c> replaces every field, so the clients must echo <see cref="ReminderSenderMemberId"/> as they read it
/// — <c>null</c> (or the field left out) <b>clears</b> the default sender. An id equal to the stored one is not validated again: a sender
/// who was suspended after it was saved must not block an unrelated edit; a changed id must be an active member.
/// </summary>
public sealed record SettingsRequest(
    string? ConfirmationMode,
    int? MinNoticeMinutes,
    int? HorizonDays,
    int? ClientCancelCutoffHours,
    bool? ReminderEnabled,
    int? ReminderOffsetHours,
    Guid? ReminderConnectionId,
    Guid? ReminderSenderMemberId);
