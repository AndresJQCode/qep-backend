using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Una línea del cuerpo de <c>POST /adjustments</c>. <c>Status</c> es el nombre del enum de saldo y <c>QuantityDelta</c>
/// viaja con signo: positivo sube el saldo, negativo lo baja.</summary>
public sealed record AdjustmentLineRequest(
    Guid LocationId,
    Guid VariantId,
    string? LotCode,
    DateOnly? ExpirationDate,
    string Status,
    decimal QuantityDelta,
    string? CapturedUom,
    decimal? CapturedQty);

/// <summary><c>AutoApprove</c> sólo se respeta si quien propone también puede aprobar: si no, el ajuste queda <c>Proposed</c> sin
/// error (un permiso que falta no es un cuerpo inválido).</summary>
public sealed record ProposeAdjustmentRequest(
    Guid WarehouseId,
    Guid? OwnerId,
    string Reason,
    string? Note,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<AdjustmentLineRequest> Lines,
    bool AutoApprove) : IIdempotentRequest;

/// <summary>Cuerpo de <c>approve</c> y de <c>post</c>. En <c>post</c> la versión además forma la clave de idempotencia del servidor.</summary>
public sealed record AdjustmentVersionRequest(long Version) : IVersionedRequest;

public sealed record RejectAdjustmentRequest(long Version, string Note);

public sealed record AdjustmentLineDto(
    Guid Id,
    Guid LocationId,
    string LocationCode,
    Guid VariantId,
    string ProductName,
    string VariantCode,
    Guid? LotId,
    string? LotCode,
    string Status,
    decimal QuantityDelta,
    string? CapturedUom,
    decimal? CapturedQty);

public sealed record AdjustmentDto(
    Guid Id,
    string Number,
    Guid WarehouseId,
    string WarehouseCode,
    Guid OwnerId,
    string Reason,
    string Status,
    string? Note,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<AdjustmentLineDto> Lines,
    Guid? BatchId,
    Guid ProposedBy,
    DateTimeOffset ProposedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? PostedAt,
    DateTimeOffset? RejectedAt,
    string? RejectNote,
    long Version);

public sealed record AdjustmentPageDto(IReadOnlyList<AdjustmentDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Arma los DTO de ajustes: los códigos de bodega y ubicación salen de las bodegas, el producto de Catalog y el código del
/// lote de los lotes (incluidos los que la misma transacción acaba de crear).</summary>
public sealed class AdjustmentDtoComposer(IWarehouseRepository warehouses, IWmsCatalogReader catalog, ILotRepository lots)
{
    public async Task<AdjustmentDto> ComposeAsync(Adjustment adjustment, Warehouse? warehouse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        var knownWarehouse = warehouse
            ?? await warehouses.GetAsync(adjustment.TenantId, adjustment.WarehouseId, cancellationToken)
            ?? throw WmsNotFound.Warehouse(adjustment.WarehouseId.Value);
        var context = await LoadAsync(adjustment.TenantId, [adjustment], cancellationToken);
        return ToDto(adjustment, knownWarehouse, context);
    }

    public async Task<IReadOnlyList<AdjustmentDto>> ComposeManyAsync(
        Guid tenantId, IReadOnlyList<Adjustment> adjustments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        if (adjustments.Count == 0)
        {
            return [];
        }

        var byId = (await warehouses.ListAsync(tenantId, includeInactive: true, cancellationToken)).ToDictionary(warehouse => warehouse.Id);
        var context = await LoadAsync(tenantId, adjustments, cancellationToken);
        return adjustments.Select(adjustment => ToDto(
            adjustment,
            byId.GetValueOrDefault(adjustment.WarehouseId) ?? throw WmsNotFound.Warehouse(adjustment.WarehouseId.Value),
            context)).ToList();
    }

    private sealed record LoadedContext(IReadOnlyDictionary<Guid, WmsVariantSnapshot> Variants, IReadOnlyDictionary<Guid, Lot> Lots);

    private async Task<LoadedContext> LoadAsync(Guid tenantId, IReadOnlyList<Adjustment> adjustments, CancellationToken cancellationToken)
    {
        var lines = adjustments.SelectMany(adjustment => adjustment.Lines).ToList();
        var variantIds = lines.Select(line => line.VariantId).Distinct().ToList();
        var lotIds = lines.Where(line => line.LotId is not null).Select(line => line.LotId!.Value.Value).Distinct().ToList();
        var variants = (await catalog.GetVariantsAsync(tenantId, variantIds, cancellationToken)).ToDictionary(snapshot => snapshot.VariantId);
        var lotsById = await lots.GetManyAsync(tenantId, lotIds, cancellationToken);
        return new LoadedContext(variants, lotsById);
    }

    private static AdjustmentDto ToDto(Adjustment adjustment, Warehouse warehouse, LoadedContext context) =>
        new(
            adjustment.Id,
            adjustment.Number,
            adjustment.WarehouseId.Value,
            warehouse.Code,
            adjustment.OwnerId.Value,
            adjustment.Reason.ToString(),
            adjustment.Status.ToString(),
            adjustment.Note,
            adjustment.OccurredAt,
            adjustment.Lines.OrderBy(line => line.Position).Select(line => ToLineDto(line, warehouse, context)).ToList(),
            adjustment.BatchId,
            adjustment.ProposedBy,
            adjustment.ProposedAt,
            adjustment.ApprovedBy,
            adjustment.ApprovedAt,
            adjustment.PostedAt,
            adjustment.RejectedAt,
            adjustment.RejectNote,
            adjustment.Version);

    private static AdjustmentLineDto ToLineDto(AdjustmentLine line, Warehouse warehouse, LoadedContext context)
    {
        var variant = context.Variants.GetValueOrDefault(line.VariantId);
        var lot = line.LotId is { } lotId ? context.Lots.GetValueOrDefault(lotId.Value) : null;
        return new AdjustmentLineDto(
            line.Id,
            line.LocationId.Value,
            warehouse.FindLocation(line.LocationId)?.Code ?? string.Empty,
            line.VariantId,
            variant?.ProductName ?? string.Empty,
            variant?.Code ?? string.Empty,
            line.LotId?.Value,
            lot?.Code,
            line.Status.ToString(),
            line.QuantityDelta,
            line.CapturedUom,
            line.CapturedQty);
    }
}
