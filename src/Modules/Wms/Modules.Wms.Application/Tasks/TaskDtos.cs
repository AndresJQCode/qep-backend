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

public sealed record TaskStatusSliceDto(string Status, int Count);

public sealed record TaskTypeSliceDto(string Type, int Open);

/// <summary>Quién tiene una tarea en curso. <c>Since</c> es cuándo la reclamó.</summary>
public sealed record TaskOperatorDto(
    Guid MemberId,
    string Name,
    Guid TaskId,
    string TaskNumber,
    string Type,
    DateTimeOffset? Since,
    DateTimeOffset? LastActivityAt);

/// <summary>Una tarea abierta asignada a alguien que ya no está activo o perdió el permiso del tipo (BFF, revisión T1 M1). La
/// pantalla no puede saberlo: no conoce Tenancy ni los permisos de nadie, y sin esta lista la tarea espera para siempre a una persona
/// que no puede reclamarla. Viene completa, sin paginar, en orden de número, para que el supervisor la reasigne.</summary>
public sealed record TaskStaleAssignmentDto(Guid TaskId, string TaskNumber, Guid MemberId, string Name);

/// <summary>El tablero de una bodega (BFF, spec 2026-11-26 §6.10). <c>ByStatus</c> y <c>ByType</c> vienen completos, también en cero:
/// la pantalla no conoce los enums. <c>StaleAssignments</c> viene completa: ver <see cref="TaskStaleAssignmentDto"/>.</summary>
public sealed record TaskBoardDto(
    Guid WarehouseId,
    IReadOnlyList<TaskStatusSliceDto> ByStatus,
    IReadOnlyList<TaskTypeSliceDto> ByType,
    IReadOnlyList<TaskOperatorDto> Operators,
    IReadOnlyList<TaskStaleAssignmentDto> StaleAssignments);

public sealed record TaskAssigneeDto(Guid MemberId, string Name);

public sealed record TaskAssigneesDto(IReadOnlyList<TaskAssigneeDto> Items);

/// <summary>La tarea en curso (con su token) y las asignadas que esperan. El operario no ve el pool (DA-09).</summary>
public sealed record MyTasksDto(TaskDto? Current, IReadOnlyList<TaskSummaryDto> Assigned);

/// <summary><c>Task</c> nulo: no hay nada para hacer, y la ruta responde 204.</summary>
public sealed record NextTaskResultDto(TaskDto? Task);

public sealed record TaskLeaseDto(DateTimeOffset LeaseExpiresAt);

public sealed record NextTaskRequest(Guid WarehouseId, string? DeviceId);

public sealed record ClaimTaskRequest(string? DeviceId);

public sealed record HeartbeatTaskRequest(Guid LeaseToken);

/// <summary><c>Reason</c> es el nombre del motivo: <c>ShiftEnd</c>, <c>CannotReach</c>, <c>Skipped</c> u <c>Other</c>.</summary>
public sealed record UnclaimTaskRequest(Guid LeaseToken, string? Reason);

/// <summary>Lo que la app escaneó. <c>OccurredAt</c> es la hora del escaneo, no la del envío; <c>OverrideReason</c> sólo hace falta
/// si el destino no es el planeado.</summary>
public sealed record ConfirmTaskLineRequest(
    Guid LeaseToken,
    Guid FromLocationId,
    Guid ToLocationId,
    Guid? LotId,
    decimal Quantity,
    string? OverrideReason,
    DateTimeOffset? OccurredAt,
    string? DeviceId) : IIdempotentRequest;

/// <summary>Cuerpo de <c>publish</c> y <c>unassign</c>. Las acciones del supervisor llevan la versión que leyó (412 si cambió).
/// </summary>
public sealed record TaskVersionRequest(long Version);

public sealed record HoldTaskRequest(long Version, string? Reason);

/// <summary>El motivo es obligatorio si la tarea estaba en curso.</summary>
public sealed record AssignTaskRequest(Guid MemberId, long Version, string? Reason);

public sealed record ChangeTaskPriorityRequest(int Priority, bool Urgent, long Version);

/// <summary>Cuerpo de <c>cancel</c> y de <c>lines/{lineId}/cancel</c>.</summary>
public sealed record CancelTaskRequest(string? Reason, long Version);
