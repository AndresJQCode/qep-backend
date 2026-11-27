using System.Diagnostics;
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record PostAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version) : ICommand<AdjustmentDto>;

/// <summary>
/// Contabiliza un ajuste aprobado contra el ledger. Ruta SK (S2): la clave es <c>adjustment:{id}:post:{version}</c>, que deja el
/// filtro en <see cref="IdempotencyScope"/>. Una sola llamada al ledger por transacción (ver <see cref="IInventoryLedger"/>).
/// </summary>
public sealed class PostAdjustmentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IAdjustmentRepository adjustments,
    IWarehouseRepository warehouses,
    IInventoryLedger ledger,
    WmsCaller caller,
    IWmsEventPublisher events,
    IWmsConfirmMetrics metrics,
    AdjustmentDtoComposer composer,
    IIncidentRepository incidents,
    CountVarianceFreshness freshness)
    : ICommandHandler<PostAdjustmentCommand, AdjustmentDto>
{
    public async Task<AdjustmentDto> HandleAsync(PostAdjustmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, ct: cancellationToken);
        var key = scope.Key ?? throw new InvalidOperationException("Posting an adjustment must run behind the server idempotency key.");

        var started = Stopwatch.GetTimestamp();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (!await unitOfWork.TryLockDocumentAsync(command.AdjustmentId, cancellationToken))
        {
            throw new ResourceConflictException(WmsErrorCodes.DocumentBusy, "Este ajuste se está procesando en este momento. Intenta de nuevo en unos segundos.");
        }

        var adjustment = await adjustments.GetAsync(command.TenantId, command.AdjustmentId, cancellationToken)
            ?? throw WmsNotFound.Adjustment(command.AdjustmentId);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, adjustment.OwnerId.Value, adjustment.WarehouseId.Value, cancellationToken);
        WmsVersions.Ensure(adjustment.Version, command.Version);
        adjustment.EnsurePostable();

        var adjustmentLocation = await warehouses.GetVirtualLocationAsync(
                command.TenantId, adjustment.WarehouseId, LocationRole.Adjustment, cancellationToken)
            ?? throw new InvalidOperationException($"Warehouse '{adjustment.WarehouseId.Value}' has no ADJUSTMENT location.");

        var now = clock.UtcNow;
        var occurredAt = adjustment.OccurredAt ?? now;
        var entries = AdjustmentPosting.EntriesFor(adjustment, adjustmentLocation, occurredAt, MovementReason.Adjustment);
        var actorId = await caller.RequireAsync(command.TenantId, cancellationToken);

        // Un ajuste de conteo sólo vale con el saldo que el conteo vio: si cambió se rechaza solo (422 count_stale, ya guardado). Con el
        // saldo igual, se bloquean los saldos en el orden del ledger y se vuelve a mirar, para que nada cambie hasta el movimiento.
        var variance = await incidents.FindByAdjustmentAsync(command.TenantId, adjustment.Id, cancellationToken);
        if (variance is not null)
        {
            await freshness.EnsureFreshAsync(adjustment, variance, now, cancellationToken);
            await freshness.LockBalancesAsync(
                adjustment, variance, [.. entries.SelectMany(entry => new[] { entry.From, entry.To })], cancellationToken);
        }

        // El ledger vuelve a validar la ventana de occurredAt: un ajuste propuesto hace 29 días puede estar fuera al contabilizarse.
        var posting = await ledger.PostAsync(
            new LedgerBatch(command.TenantId, Guid.CreateVersion7(), key, new LedgerActor(actorId, LedgerActorType.Human, null), entries),
            cancellationToken);

        adjustment.MarkPosted(posting.BatchId, now);

        // La CountVariance que este ajuste cierra se resuelve en la misma transacción, con el lote del ledger.
        if (variance is not null)
        {
            variance.ResolveByAdjustment(posting.BatchId, actorId, adjustment.Number, now);
        }

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            AdjustmentPosted,
            Resources.Adjustment,
            adjustment.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = adjustment.OwnerId.Value.ToString(),
                ["number"] = adjustment.Number,
                ["batchId"] = posting.BatchId.ToString(),
                ["lineCount"] = adjustment.Lines.Count.ToString(CultureInfo.InvariantCulture),
            },
            now);
        events.PublishAdjustmentPosted(adjustment, now);

        var dto = await composer.ComposeAsync(adjustment, null, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        metrics.RecordConfirmDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return dto;
    }
}
