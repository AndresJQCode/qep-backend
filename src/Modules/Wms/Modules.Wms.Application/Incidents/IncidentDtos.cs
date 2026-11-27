namespace Modules.Wms.Application;

/// <summary>
/// La fila de la bandeja de novedades (BFF, spec 2026-11-26 §8.5). Los códigos de ubicación y de variante, el producto, el número
/// de la tarea y los nombres de las personas se resuelven en el servidor: la pantalla no hace un viaje por fila.
/// <c>ReportedByName</c> de una novedad del sistema (<c>ReportedBy = Guid.Empty</c>) es «Sistema». Los enums viajan con su nombre.
/// </summary>
public sealed record IncidentSummaryDto(
    Guid Id,
    string Number,
    string Type,
    string Status,
    Guid WarehouseId,
    Guid OwnerId,
    Guid? LocationId,
    string? LocationCode,
    Guid? VariantId,
    string? VariantCode,
    string? ProductName,
    decimal? ExpectedQty,
    decimal? ActualQty,
    Guid ReportedBy,
    string ReportedByName,
    DateTimeOffset ReportedAt,
    Guid? SourceTaskId,
    string? SourceTaskNumber,
    Guid? VerificationTaskId,
    Guid? AdjustmentId,
    long Version);

public sealed record IncidentEvidenceDto(Guid FileId, string Kind, string? RawValue);

public sealed record IncidentEventDto(string Kind, Guid? MemberId, string? MemberName, string? Detail, DateTimeOffset OccurredAt);

/// <summary>
/// La ficha de una novedad (BFF): la fila de la bandeja más lo que sólo la ficha necesita (<c>RawCode</c>, <c>RawPayload</c>, la
/// evidencia y la historia). <c>AllowedResolutions</c> y <c>CanBeRejected</c> salen del catálogo <b>y de la novedad</b>: la pantalla
/// no conoce el catálogo y, sin esto, pintaría botones que responden 422. <c>AcceptIntoStock</c> sólo se ofrece si lo encontrado es
/// mayor que cero y la línea de origen tiene destino (un conflicto de un conteo o de un faltante con cero encontrado no tiene nada que
/// aplicar: sólo se rechaza). Una novedad ya cerrada no ofrece nada. La historia viene en el orden del servidor.
/// </summary>
public sealed record IncidentDto(
    Guid Id,
    string Number,
    string Type,
    string Status,
    Guid WarehouseId,
    Guid OwnerId,
    Guid? LocationId,
    string? LocationCode,
    Guid? VariantId,
    string? VariantCode,
    string? ProductName,
    decimal? ExpectedQty,
    decimal? ActualQty,
    Guid ReportedBy,
    string ReportedByName,
    DateTimeOffset ReportedAt,
    Guid? SourceTaskId,
    string? SourceTaskNumber,
    Guid? VerificationTaskId,
    Guid? AdjustmentId,
    long Version,
    Guid? LotId,
    string? LotCode,
    string? StockStatus,
    string? RawCode,
    string? RawPayload,
    string? Note,
    string? DeviceId,
    string? Resolution,
    Guid? ResolvedBy,
    string? ResolvedByName,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote,
    Guid? BatchId,
    Guid? ParentIncidentId,
    IReadOnlyList<string> AllowedResolutions,
    bool CanBeRejected,
    IReadOnlyList<IncidentEvidenceDto> Evidence,
    IReadOnlyList<IncidentEventDto> Events);

public sealed record IncidentPageDto(IReadOnlyList<IncidentSummaryDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Lo que el operario declara sin una línea de tarea (spec §8.5, DA-49): sólo <c>UnreadableLabel</c> y <c>LocationMismatch</c>.
/// <c>PhotoFileIds</c> son archivos <c>WmsEvidence</c> del tenant, en cualquier estado, a lo sumo 5 distintos. <c>OccurredAt</c> es la
/// hora del escaneo (ventana del ledger). Ruta IK.
/// </summary>
public sealed record ReportIncidentRequest(
    Guid WarehouseId,
    string? Type,
    Guid? LocationId,
    Guid? VariantId,
    Guid? LotId,
    decimal? Quantity,
    string? RawCode,
    string? Note,
    IReadOnlyList<Guid>? PhotoFileIds,
    Guid? SourceTaskId,
    DateTimeOffset? OccurredAt,
    string? DeviceId) : IIdempotentRequest;

/// <summary>Cuerpo de <c>review</c>: la versión que el supervisor leyó (412 si cambió).</summary>
public sealed record IncidentVersionRequest(long Version) : IVersionedRequest;

/// <summary>Cuerpo de <c>resolve</c>. En T2 no lleva destino ni lote (DA-19 del plan): las resoluciones de T2 no los usan.</summary>
public sealed record ResolveIncidentRequest(long Version, string? Resolution, string? Note) : IVersionedRequest;

public sealed record RejectIncidentRequest(long Version, string? Note) : IVersionedRequest;

/// <summary><c>BatchId</c> es el lote del ledger si la resolución movió stock. <c>CreatedTaskIds</c> viene siempre, y vacía en T2.</summary>
public sealed record IncidentResolutionDto(Guid? BatchId, IReadOnlyList<Guid> CreatedTaskIds);
