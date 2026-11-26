namespace Modules.Wms.Application;

/// <summary>El cuerpo de <c>POST /tasks</c>: un movimiento interno. Sin dueño es el Self del tenant; sin <c>Publish</c> se publica.
/// </summary>
public sealed record CreateMoveTaskRequest(
    Guid WarehouseId,
    Guid? OwnerId,
    int? Priority,
    DateTimeOffset? DueAt,
    string? Note,
    bool? Publish,
    Guid? AssignToMemberId,
    IReadOnlyList<MoveTaskLineRequest>? Lines) : IIdempotentRequest;

/// <summary><c>StockStatus</c> es el nombre del estado de saldo; sin él, <c>Available</c>. La cantidad va en unidad base.</summary>
public sealed record MoveTaskLineRequest(
    Guid VariantId,
    Guid? LotId,
    string? StockStatus,
    Guid FromLocationId,
    Guid ToLocationId,
    decimal Quantity);

/// <summary>Una tarea en un listado. <c>ClaimedBy*</c> sólo viene mientras está <c>InProgress</c>: el reclamo vencido que la base
/// conserva para la confirmación tardía no es de nadie en pantalla.</summary>
public sealed record TaskSummaryDto(
    Guid Id,
    string Number,
    string Type,
    string Status,
    Guid WarehouseId,
    Guid OwnerId,
    int Priority,
    bool Urgent,
    int EffectivePriority,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReleasedAt,
    Guid? AssignedMemberId,
    string? AssignedMemberName,
    Guid? ClaimedByMemberId,
    string? ClaimedByMemberName,
    int LineCount,
    int OpenLineCount,
    string FirstFromLocationCode,
    DateTimeOffset? LastActivityAt,
    long Version);

public sealed record TaskLineDto(
    Guid Id,
    int Position,
    Guid VariantId,
    string VariantCode,
    string ProductName,
    Guid? LotId,
    string? LotCode,
    string StockStatus,
    Guid FromLocationId,
    string FromLocationCode,
    Guid? ToLocationId,
    string? ToLocationCode,
    decimal QuantityPlanned,
    decimal QuantityConfirmed,
    decimal QuantityRemaining,
    string LineStatus,
    string BaseUom);

public sealed record TaskEventDto(
    string Kind,
    Guid? MemberId,
    string? MemberName,
    string? DeviceId,
    Guid? LineId,
    decimal? Quantity,
    string? Detail,
    DateTimeOffset OccurredAt);

/// <summary>
/// Una tarea con sus líneas (BFF, spec 2026-11-26 §6.11). <c>LeaseToken</c> sólo viaja a quien la tiene reclamada: nadie más puede
/// firmar sus confirmaciones. <c>Events</c> sólo viene en <c>GET /tasks/{id}</c>, la ficha del supervisor; en las demás respuestas va
/// vacío porque la app del operario no lo dibuja y cada byte cuenta con mala señal.
/// </summary>
public sealed record TaskDto(
    Guid Id,
    string Number,
    string Type,
    string Status,
    Guid WarehouseId,
    Guid OwnerId,
    int Priority,
    bool Urgent,
    int EffectivePriority,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReleasedAt,
    Guid? AssignedMemberId,
    string? AssignedMemberName,
    Guid? ClaimedByMemberId,
    string? ClaimedByMemberName,
    int LineCount,
    int OpenLineCount,
    string FirstFromLocationCode,
    DateTimeOffset? LastActivityAt,
    long Version,
    string SourceType,
    Guid? SourceId,
    string? Note,
    Guid? LeaseToken,
    DateTimeOffset? LeaseExpiresAt,
    IReadOnlyList<TaskLineDto> Lines,
    IReadOnlyList<TaskEventDto> Events);

public sealed record TaskPageDto(IReadOnlyList<TaskSummaryDto> Items, int Page, int PageSize, int TotalCount);
