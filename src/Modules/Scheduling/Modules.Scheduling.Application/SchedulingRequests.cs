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
public sealed record SettingsRequest(
    string? ConfirmationMode,
    int? MinNoticeMinutes,
    int? HorizonDays,
    int? ClientCancelCutoffHours,
    bool? ReminderEnabled,
    int? ReminderOffsetHours,
    Guid? ReminderConnectionId,
    Guid? ReminderTemplateId);
