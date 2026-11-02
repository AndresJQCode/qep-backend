using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Ruta IK, permiso <c>wms.adjustment.propose</c> (§24.2 y §24.3): aprobar y postear no hace falta, el ajuste de la carga nace
/// aprobado y posteado por el mismo camino. <c>Version</c> es la de la bodega que el preview devolvió; si cambió desde entonces, 412 sin tocar nada.</summary>
public sealed record CommitInitialBalancesRequest(Guid PreviewId, long Version) : IIdempotentRequest;

public sealed record CommitInitialBalancesCommand(Guid TenantId, Guid PreviewId, long Version) : ICommand<InitialBalanceCommitDto>;

public sealed record InitialBalanceCommitDto(Guid AdjustmentId, Guid BatchId, int MovementCount);

/// <summary>
/// RF-024, segunda mitad: contabiliza un preview sin problemas como un ajuste <c>InitialLoad</c> ya aprobado y posteado, en un solo
/// batch del ledger. Cada fila es exactamente un movimiento (desde <c>ADJUSTMENT</c> hacia la ubicación: la cantidad siempre es positiva,
/// así que nunca hay contrapartida aparte), y el preview no pasa de <see cref="LedgerBatch.MaxEntries"/> filas: una sola llamada al ledger
/// por transacción, sin partir el lote.
/// </summary>
public sealed class CommitInitialBalancesHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    ILotRepository lots,
    IVariantProfileRepository profiles,
    IAdjustmentRepository adjustments,
    IImportPreviewStore store,
    IInventoryLedger ledger,
    IStockReadQueries stockQueries,
    WmsCaller caller,
    IWmsEventPublisher events,
    IWmsConfirmMetrics metrics)
    : ICommandHandler<CommitInitialBalancesCommand, InitialBalanceCommitDto>
{
    public async Task<InitialBalanceCommitDto> HandleAsync(CommitInitialBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.AdjustmentPropose, ct: cancellationToken);
        var key = scope.Key ?? throw new InvalidOperationException("Committing an import must run behind the Idempotency-Key.");

        var started = Stopwatch.GetTimestamp();
        // S26: el await using deshace todo si SaveChangesAsync lanza OperationReplayedException (la perdedora de dos peticiones iguales).
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (!await unitOfWork.TryLockDocumentAsync(command.PreviewId, cancellationToken))
        {
            throw new ResourceConflictException(WmsErrorCodes.DocumentBusy, "Esta carga se está procesando en este momento. Intenta de nuevo en unos segundos.");
        }

        var now = clock.UtcNow;
        var preview = await store.GetAsync(command.TenantId, command.PreviewId, cancellationToken)
            ?? throw WmsNotFound.ImportPreview(command.PreviewId);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentPropose, preview.OwnerId.Value, preview.WarehouseId.Value, cancellationToken);

        if (preview.IsExpiredAt(now))
        {
            throw new WmsDomainException(WmsErrorCodes.ImportPreviewExpired, "La vista previa venció. Sube el archivo de nuevo.");
        }

        if (!preview.CanCommit)
        {
            throw new WmsDomainException(
                WmsErrorCodes.ImportInvalidFile, "La vista previa tiene filas con problemas. Corrige el archivo y súbelo de nuevo.");
        }

        var warehouse = await warehouses.GetAsync(command.TenantId, preview.WarehouseId, cancellationToken)
            ?? throw WmsNotFound.Warehouse(preview.WarehouseId.Value);
        WmsVersions.Ensure(warehouse.Version, command.Version);
        _ = await owners.FindAsync(command.TenantId, preview.OwnerId, cancellationToken) ?? throw WmsNotFound.Owner(preview.OwnerId.Value);

        var rows = JsonSerializer.Deserialize<List<InitialBalanceStoredRow>>(preview.RowsJson, InitialBalanceJson.Options)
            ?? throw new InvalidOperationException($"Import preview '{preview.Id}' has no rows.");
        // I-7: el preview ya revisó el saldo, pero dos previews hechos antes del primer commit pasan los dos. El lock es por tenant e
        // importación y va antes de cualquier escritura (lotes, numeración, ledger) para no cruzarse con ellas; el segundo commit espera
        // al primero y entonces ve su saldo. Falla como un commit con problemas, y el preview queda disponible.
        await unitOfWork.LockAsync("import:" + command.TenantId.ToString("D"), cancellationToken);
        await EnsureNoStockYetAsync(command.TenantId, preview.OwnerId.Value, rows, cancellationToken);

        var lines = await BuildLinesAsync(command.TenantId, preview.OwnerId.Value, warehouse, rows, now, cancellationToken);

        var number = await adjustments.NextNumberAsync(command.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var adjustment = Adjustment.Propose(
            command.TenantId,
            number,
            preview.OwnerId,
            warehouse.Id,
            AdjustmentReason.InitialLoad,
            null,
            null,
            lines,
            executionContext.SubjectId,
            now);
        adjustment.Approve(executionContext.SubjectId, now);
        adjustments.Add(adjustment);

        var adjustmentLocation = await warehouses.GetVirtualLocationAsync(
                command.TenantId, warehouse.Id, LocationRole.Adjustment, cancellationToken)
            ?? throw new InvalidOperationException($"Warehouse '{warehouse.Id.Value}' has no ADJUSTMENT location.");
        var entries = AdjustmentPosting.EntriesFor(adjustment, adjustmentLocation, now, MovementReason.InitialLoad);

        // Una fila, un movimiento: si esto dejara de cumplirse el tope de 500 filas ya no garantizaría un solo batch.
        if (entries.Count != adjustment.Lines.Count)
        {
            throw new InvalidOperationException("An initial load line must map to exactly one ledger entry.");
        }

        var actorId = await caller.RequireAsync(command.TenantId, cancellationToken);
        var posting = await ledger.PostAsync(
            new LedgerBatch(command.TenantId, Guid.CreateVersion7(), key, new LedgerActor(actorId, LedgerActorType.Human, null), entries),
            cancellationToken);

        adjustment.MarkPosted(posting.BatchId, now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            StockImported,
            Resources.Import,
            preview.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = preview.OwnerId.Value.ToString(),
                ["warehouseId"] = preview.WarehouseId.Value.ToString(),
                ["adjustmentId"] = adjustment.Id.ToString(),
                // Quien confirma puede no ser quien preparó la carga: la auditoría tiene que decir las dos cosas.
                ["preparedBy"] = preview.RequestedBy.ToString(),
                ["rowCount"] = rows.Count.ToString(CultureInfo.InvariantCulture),
            },
            now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            AdjustmentPosted,
            Resources.Adjustment,
            adjustment.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = preview.OwnerId.Value.ToString(),
                ["number"] = adjustment.Number,
                ["batchId"] = posting.BatchId.ToString(),
                ["lineCount"] = adjustment.Lines.Count.ToString(CultureInfo.InvariantCulture),
            },
            now);
        events.PublishAdjustmentPosted(adjustment, now);
        store.Remove(preview);

        var dto = new InitialBalanceCommitDto(adjustment.Id, posting.BatchId, posting.MovementIds.Count);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        metrics.RecordConfirmDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return dto;
    }

    private async Task EnsureNoStockYetAsync(
        Guid tenantId, Guid ownerId, List<InitialBalanceStoredRow> rows, CancellationToken cancellationToken)
    {
        var variantIds = rows.Select(stored => stored.Row.VariantId!.Value).Distinct().ToList();
        var locationIds = rows.Select(stored => stored.Row.LocationId!.Value).Distinct().ToList();
        var withStock = await stockQueries.FindNonZeroBalanceKeysAsync(tenantId, ownerId, variantIds, locationIds, cancellationToken);
        if (withStock.Count == 0)
        {
            return;
        }

        // Un lote que todavía no existe no puede tener saldo: solo los que ya existen se comparan por id.
        var lotIds = new Dictionary<(Guid VariantId, string LotCode), Guid?>();
        foreach (var stored in rows)
        {
            var row = stored.Row;
            Guid? lotId = null;
            if (row.LotCode is { } lotCode)
            {
                var key = (row.VariantId!.Value, lotCode);
                if (!lotIds.TryGetValue(key, out lotId))
                {
                    lotId = (await lots.FindAsync(tenantId, ownerId, key.Item1, lotCode, cancellationToken))?.Id.Value;
                    lotIds[key] = lotId;
                }

                if (lotId is null)
                {
                    continue;
                }
            }

            if (withStock.Contains((row.VariantId!.Value, row.LocationId!.Value, lotId)))
            {
                throw new WmsDomainException(
                    WmsErrorCodes.ImportInvalidFile,
                    $"La fila {row.Row} ya tiene saldo: otra carga se confirmó antes que esta. Sube el archivo de nuevo.");
            }
        }
    }

    private async Task<List<AdjustmentLine>> BuildLinesAsync(
        Guid tenantId,
        Guid ownerId,
        Warehouse warehouse,
        List<InitialBalanceStoredRow> rows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var variantIds = rows.Where(stored => stored.Row.VariantId is not null).Select(stored => stored.Row.VariantId!.Value).Distinct().ToList();
        var variantProfiles = (await profiles.ListAsync(tenantId, variantIds, cancellationToken)).ToDictionary(profile => profile.VariantId);

        var created = new Dictionary<(Guid VariantId, string Code), Lot>();
        var lines = new List<AdjustmentLine>(rows.Count);
        foreach (var stored in rows)
        {
            var row = stored.Row;
            var variantId = row.VariantId ?? throw new InvalidOperationException($"Row {row.Row} of a committable preview has no variant.");
            var locationId = row.LocationId ?? throw new InvalidOperationException($"Row {row.Row} of a committable preview has no location.");
            var location = warehouse.FindLocation(new LocationId(locationId)) ?? throw WmsNotFound.Location(locationId);

            LotId? lotId = null;
            if (row.LotCode is { } lotCode)
            {
                var lotKey = (variantId, lotCode);
                if (!created.TryGetValue(lotKey, out var lot))
                {
                    lot = await lots.FindAsync(tenantId, ownerId, variantId, lotCode, cancellationToken);
                    if (lot is null)
                    {
                        lot = Lot.Create(
                            tenantId, ownerId, variantId, lotCode, row.ExpirationDate, null, variantProfiles.GetValueOrDefault(variantId)?.TracksExpiry == true, now);
                        lots.Add(lot);
                        created[lotKey] = lot;
                    }
                }

                lotId = lot.Id;
            }

            var captured = row.Uom is not null && stored.CapturedQuantity is not null;
            lines.Add(AdjustmentLine.Create(
                location.Id,
                variantId,
                lotId,
                StockStatus.Available,
                row.Quantity,
                captured ? row.Uom : null,
                captured ? stored.CapturedQuantity : null));
        }

        // La FK de la línea al lote no está en el modelo de EF: los lotes nuevos se escriben antes de que exista el ajuste.
        if (created.Count > 0)
        {
            await unitOfWork.FlushAsync(cancellationToken);
        }

        return lines;
    }
}
