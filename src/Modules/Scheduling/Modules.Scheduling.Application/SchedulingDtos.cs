namespace Modules.Scheduling.Application;

/// <param name="IsMain">Una sola por tenant; la pantalla no ofrece archivarla (el servidor tampoco lo permite).</param>
public sealed record LocationDto(Guid Id, string Name, bool IsMain, bool Archived, long Version);

/// <param name="MemberName">El nombre del miembro vinculado, ya resuelto: la pantalla no consulta Personas para pintar la fila.</param>
public sealed record ResourceDto(
    Guid Id, Guid LocationId, string DisplayName, Guid? MemberId, string? MemberName, bool Archived, long Version);

public sealed record ServiceDto(
    Guid Id, string Name, int DurationMinutes, int BufferMinutes, bool BookableByChannel, bool Archived, long Version);

/// <param name="ConfirmationMode">El nombre del enum: <c>Automatic</c> o <c>Manual</c>.</param>
public sealed record SettingsDto(
    string ConfirmationMode,
    int MinNoticeMinutes,
    int HorizonDays,
    int ClientCancelCutoffHours,
    bool ReminderEnabled,
    int ReminderOffsetHours,
    Guid? ReminderConnectionId,
    Guid? ReminderTemplateId,
    long Version);

/// <summary>
/// <c>GET setup</c>: lo que el asistente de primer uso necesita en una llamada. <c>Layout</c> lo decide el backend contando sedes y
/// recursos activos (<c>Personal</c> con una sede y un recurso; <c>Consolidated</c> en cualquier otro caso): el cliente no cuenta.
/// </summary>
public sealed record SetupDto(bool HasService, bool HasResource, bool HasBlock, string Layout);
