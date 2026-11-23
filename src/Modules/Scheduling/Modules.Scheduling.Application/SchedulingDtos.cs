namespace Modules.Scheduling.Application;

/// <param name="IsMain">Una sola por tenant; la pantalla no ofrece archivarla (el servidor tampoco lo permite).</param>
public sealed record LocationDto(Guid Id, string Name, bool IsMain, bool Archived, long Version);

/// <param name="MemberName">El nombre del miembro vinculado, ya resuelto: la pantalla no consulta Personas para pintar la fila.</param>
public sealed record ResourceDto(
    Guid Id, Guid LocationId, string DisplayName, Guid? MemberId, string? MemberName, bool Archived, long Version);

public sealed record ServiceDto(
    Guid Id, string Name, int DurationMinutes, int BufferMinutes, bool BookableByChannel, bool Archived, long Version);

public sealed record TimeRangeDto(int From, int To);

public sealed record WeekdayHoursDto(int Day, IReadOnlyList<TimeRangeDto> Ranges);

public sealed record DateHoursDto(DateOnly Date, IReadOnlyList<TimeRangeDto> Ranges);

/// <summary>
/// <c>Weekly</c> siempre viaja (vacía en una franja puntual) y <c>ServiceIds</c> y <c>ResourceIds</c> vuelven enteros y en el orden guardado:
/// el de los recursos es el de la asignación «primero libre», y el formulario los repinta tal cual.
/// </summary>
public sealed record BlockDto(
    Guid Id,
    Guid LocationId,
    string Name,
    string Kind,
    IReadOnlyList<WeekdayHoursDto> Weekly,
    DateHoursDto? OneOff,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool ClosesOnHolidays,
    int CapacityPerResource,
    string? SessionMode,
    bool AllowClientChoice,
    string AssignmentRule,
    IReadOnlyList<Guid> ServiceIds,
    IReadOnlyList<Guid> ResourceIds,
    bool Archived,
    long Version);

/// <param name="ResourceId">Nulo es un bloqueo de toda la sede.</param>
public sealed record ClosureDto(
    Guid Id, Guid LocationId, Guid? ResourceId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason, long Version);

/// <param name="IsPublished">Siempre <c>true</c> aquí: sólo las sesiones publicadas se crean y se quitan por esta ruta.</param>
public sealed record PublishedSessionDto(
    Guid Id,
    Guid ResourceId,
    Guid ServiceId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int Capacity,
    int BookedCount,
    bool IsPublished,
    long Version);

/// <param name="ConfirmationMode">El nombre del enum: <c>Automatic</c> o <c>Manual</c>.</param>
/// <param name="ReminderSenderMemberName">El nombre ya resuelto del remitente por defecto (D-18): la pantalla no consulta Personas para nombrarlo.
/// Un remitente que después quedó suspendido sigue nombrado, para que la pantalla lo muestre y pida elegir otro. Nulo sin remitente.</param>
public sealed record SettingsDto(
    string ConfirmationMode,
    int MinNoticeMinutes,
    int HorizonDays,
    int ClientCancelCutoffHours,
    bool ReminderEnabled,
    int ReminderOffsetHours,
    Guid? ReminderConnectionId,
    Guid? ReminderSenderMemberId,
    string? ReminderSenderMemberName,
    ReminderTemplateStateDto? ReminderTemplate,
    long Version);

/// <summary>El estado de la plantilla de recordatorio de la conexión guardada (spec 2026-11-23 §2): <c>Status</c> es el de Meta tal cual
/// (<c>APPROVED</c>, <c>PENDING</c>, <c>REJECTED</c>…) o <c>Missing</c> si la conexión no tiene la fila.</summary>
public sealed record ReminderTemplateStateDto(string Status, string? StatusReason);

/// <summary>
/// <c>GET setup</c>: lo que el asistente de primer uso necesita en una llamada. <c>Layout</c> lo decide el backend contando sedes y
/// recursos activos (<c>Personal</c> con una sede y un recurso; <c>Consolidated</c> en cualquier otro caso): el cliente no cuenta.
/// </summary>
public sealed record SetupDto(bool HasService, bool HasResource, bool HasBlock, string Layout);
