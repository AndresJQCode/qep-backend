using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>El cuerpo es opcional: sin nota, el compensatorio no lleva motivo.</summary>
public sealed record ReverseMovementRequest(string? Note) : IIdempotentRequest;

public sealed record ReverseMovementCommand(Guid TenantId, Guid MovementId, string? Note) : ICommand<MovementDto>
{
    public const int NoteMaxLength = 500;

    public static ReverseMovementCommand From(Guid tenantId, Guid movementId, ReverseMovementRequest? request) =>
        new(tenantId, movementId, string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim());
}

public sealed class ReverseMovementValidator : AbstractValidator<ReverseMovementCommand>
{
    public ReverseMovementValidator()
    {
        RuleFor(command => command.Note).MaximumLength(ReverseMovementCommand.NoteMaxLength);
    }
}

/// <summary>
/// RF-025: no se edita un movimiento, se compensa con otro que va al revés, apunta al original (con su fecha, S4) y comparte su lote.
/// Ruta IK: un movimiento no tiene <c>version</c>, así que no hay clave de servidor que derivar. Una sola llamada al ledger por
/// transacción.
/// </summary>
public sealed class ReverseMovementHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IMovementReader movements,
    IInventoryLedger ledger,
    WmsCaller caller,
    IWarehouseRepository warehouses,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    ILotRepository lots,
    IValidator<ReverseMovementCommand> validator)
    : ICommandHandler<ReverseMovementCommand, MovementDto>
{
    public async Task<MovementDto> HandleAsync(ReverseMovementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var key = scope.Key ?? throw new InvalidOperationException("Reversing a movement must run behind the idempotency key.");

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (!await unitOfWork.TryLockDocumentAsync(command.MovementId, cancellationToken))
        {
            throw new ResourceConflictException(WmsErrorCodes.DocumentBusy, "Alguien más está reversando este movimiento. Intenta de nuevo en un momento.");
        }

        var movement = await movements.GetAsync(command.TenantId, command.MovementId, cancellationToken)
            ?? throw WmsNotFound.Movement(command.MovementId);

        var origin = await warehouses.GetLocationAsync(command.TenantId, new LocationId(movement.FromLocationId), cancellationToken)
            ?? throw WmsNotFound.Location(movement.FromLocationId);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, movement.OwnerId, origin.WarehouseId.Value, cancellationToken);

        // Inventario básico §5.6: lo que asentó una orden de compra, un pedido o una venta se deshace desde su documento. Si no, una
        // reversa manual seguida de un «deshacer preparado» o una anulación devolvería dos veces.
        if (InventoryDocumentTypes.IsInventoryDocument(movement.DocumentType))
        {
            throw new WmsDomainException(
                WmsErrorCodes.MovementReverseFromDocument,
                "Este movimiento viene de una orden, un pedido o una venta: se deshace desde el documento, no a mano.");
        }

        // Reversar un compensatorio sería reaplicar el original por la puerta de atrás: el camino es proponer otro ajuste.
        if (movement.Reason == MovementReason.Reversal
            || await movements.IsReversedAsync(command.TenantId, command.MovementId, cancellationToken))
        {
            throw new WmsDomainException(WmsErrorCodes.MovementAlreadyReversed, "Este movimiento ya fue reversado.");
        }

        var now = clock.UtcNow;
        var compensation = LedgerEntry.Reverse(ToEntry(movement), movement.Id, movement.OccurredAt, now, command.Note);
        var actorId = await caller.RequireAsync(command.TenantId, cancellationToken);

        // El compensatorio comparte el lote del original: reversar es parte de la misma operación.
        var posting = await ledger.PostAsync(
            new LedgerBatch(command.TenantId, movement.BatchId, key, new LedgerActor(actorId, LedgerActorType.Human, null), [compensation]),
            cancellationToken);
        var reversalId = posting.MovementIds.Single();

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            MovementReversed,
            Resources.Movement,
            reversalId.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = movement.OwnerId.ToString(),
                ["reversesMovementId"] = movement.Id.ToString(),
                ["batchId"] = movement.BatchId.ToString(),
            },
            now);

        var dto = await ToDtoAsync(command.TenantId, reversalId, compensation, movement.BatchId, actorId, now, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    /// <summary>La entrada original a partir de su fila: lo que el ledger escribió es lo que el compensatorio invierte. La unidad
    /// logística de cada extremo también (D-12): sin ella el reverso compensaría el saldo suelto y no el de la unidad.</summary>
    private static LedgerEntry ToEntry(StockMovement movement) =>
        new(
            new StockKey(
                movement.OwnerId, movement.FromLocationId, movement.VariantId, movement.LotId, movement.FromStatus, movement.FromHandlingUnitId),
            new StockKey(
                movement.OwnerId, movement.ToLocationId, movement.VariantId, movement.LotId, movement.ToStatus, movement.ToHandlingUnitId),
            movement.Quantity,
            movement.CapturedUom is not null && movement.CapturedQty is { } capturedQty && movement.ConversionFactor is { } factor
                ? new LedgerCapture(movement.CapturedUom, capturedQty, factor)
                : null,
            movement.Reason,
            movement.DocumentType is not null && movement.DocumentId is { } documentId
                ? new LedgerDocument(movement.DocumentType, documentId, movement.DocumentLineId)
                : null,
            movement.OccurredAt,
            serialUnitId: movement.SerialUnitId);

    private async Task<MovementDto> ToDtoAsync(
        Guid tenantId, Guid id, LedgerEntry entry, Guid batchId, Guid actorId, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        var from = await warehouses.GetLocationAsync(tenantId, new LocationId(entry.From.LocationId), cancellationToken)
            ?? throw WmsNotFound.Location(entry.From.LocationId);
        var to = await warehouses.GetLocationAsync(tenantId, new LocationId(entry.To.LocationId), cancellationToken)
            ?? throw WmsNotFound.Location(entry.To.LocationId);
        var variants = await catalog.GetVariantsAsync(tenantId, [entry.From.VariantId], cancellationToken);
        var variant = variants.Count > 0 ? variants[0] : null;
        var profile = await profiles.FindAsync(tenantId, entry.From.VariantId, cancellationToken);
        string? lotCode = null;
        if (entry.From.LotId is { } lotId)
        {
            lotCode = (await lots.GetManyAsync(tenantId, [lotId], cancellationToken)).GetValueOrDefault(lotId)?.Code;
        }

        return new MovementDto(
            id,
            entry.OccurredAt,
            recordedAt,
            entry.Reason.ToString(),
            entry.Quantity,
            profile?.BaseUom.Value ?? WmsDefaults.BaseUom,
            entry.Captured?.Uom,
            entry.Captured?.Quantity,
            entry.Captured?.Factor,
            new MovementEndpointDto(from.Code, entry.From.Status.ToString()),
            new MovementEndpointDto(to.Code, entry.To.Status.ToString()),
            entry.From.VariantId,
            variant?.ProductName ?? string.Empty,
            variant?.Code ?? string.Empty,
            lotCode,
            Serial: null,
            entry.Document?.Type,
            entry.Document?.Id,
            DocumentNumber: null,
            batchId,
            entry.ReversesMovementId,
            actorId,
            ActorName: null,
            LedgerActorType.Human.ToString(),
            DeviceId: null);
    }
}
