using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>
/// Aplica un evento del inventario básico (spec §5.2): decide con <see cref="DocumentStockMachine"/>, aparta las líneas que no se pueden
/// mover (§5.6), reserva sin ubicación, asienta en el ledger y deja la fila del documento. Corre en la transacción del consumidor; no
/// guarda ni confirma.
/// <para><b>Orden de bloqueos</b> (consistente en todos los caminos): primero las reservas de demanda (sólo filas de
/// <c>reservations</c>), luego el ledger (saldos, y la cola como último bloqueo del lote) y, al final,
/// <see cref="IDemandReservations.EnqueueChangesAsync"/>: la cola es siempre lo último. Las líneas van ordenadas por variante.</para>
/// <para>Una reversa vuelve a donde fue el original (§5.2): se compensan exactamente los movimientos del lote guardado, y lo que se
/// reserva de nuevo sale de esos movimientos y no de las líneas actuales, en la bodega de la fila y no en la principal de hoy.</para>
/// </summary>
public sealed class InventoryEventApplier(
    ITenantModules tenantModules,
    IDocumentStockStates states,
    PrincipalWarehouseProvisioner provisioner,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    IDemandReservations reservations,
    IInventoryLedger ledger,
    IMovementReader movements,
    IInventoryEventTelemetry telemetry,
    IClock clock) : IInventoryEventApplier
{
    private static readonly LedgerActor SystemActor = new(Guid.Empty, LedgerActorType.System, null);

    public async Task ApplyAsync(InventoryMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!InventoryEventPayloads.TryParse(message.EventName, message.PayloadJson, out var evt))
        {
            throw new InvalidOperationException($"The payload of message '{message.MessageId}' is not a valid inventory event.");
        }

        var now = clock.UtcNow;
        var state = await states.FindAsync(evt.TenantId, evt.DocumentType, evt.DocumentId, cancellationToken);

        // §5.2: sólo un documento sin estado real mira el módulo; uno que ya tiene estado se aplica siempre, aunque se haya apagado.
        if ((state is null || state.Status is DocumentStockStatus.None or DocumentStockStatus.Parked)
            && !await ModuleIsOnAsync(evt.TenantId, cancellationToken))
        {
            return;
        }

        if (state is null)
        {
            state = DocumentStockState.Create(evt.TenantId, evt.DocumentType, evt.DocumentId, now);
            states.Add(state);
        }

        var decision = DocumentStockMachine.Decide(state.Status, state.LastVersion, evt.ToEvent());
        var occurredAt = ResolveOccurredAt(evt, now);
        switch (decision.Action)
        {
            case StockAction.Reserve:
                await ReserveAsync(evt, state, cancellationToken);
                break;
            case StockAction.ReplaceReservation:
                await ReplaceReservationAsync(evt, state, cancellationToken);
                break;
            case StockAction.TakeOut:
                await TakeOutAsync(evt, state, occurredAt, cancellationToken);
                break;
            case StockAction.CompensateAndReserve:
                await CompensateAsync(evt, state, occurredAt, reserveAgain: true, cancellationToken);
                break;
            case StockAction.Compensate:
                await CompensateAsync(evt, state, occurredAt, reserveAgain: false, cancellationToken);
                break;
            case StockAction.Release:
                await reservations.CloseAllAsync(evt.TenantId, evt.DocumentType, evt.DocumentId, cancellationToken);
                break;
            case StockAction.Receive:
                await PostFromPrincipalAsync(evt, state, occurredAt, MovementReason.Receipt, LocationRole.Supplier, towardsGeneral: true, cancellationToken);
                break;
            case StockAction.Ship:
                await PostFromPrincipalAsync(evt, state, occurredAt, MovementReason.Ship, LocationRole.Customer, towardsGeneral: false, cancellationToken);
                break;
            default:
                // Anulado ya descontado (decisión del owner): nada vuelve, pero una reserva que siga abierta se libera.
                if (decision.Next == DocumentStockStatus.Released && state.Status != DocumentStockStatus.Released)
                {
                    await reservations.CloseAllAsync(evt.TenantId, evt.DocumentType, evt.DocumentId, cancellationToken);
                }

                break;
        }

        state.Advance(evt.Version, decision.Next, now);

        // Al final de todo, y después del ledger: la cola es el último bloqueo de la transacción.
        await reservations.EnqueueChangesAsync(cancellationToken);
    }

    private async Task<bool> ModuleIsOnAsync(Guid tenantId, CancellationToken ct) =>
        (await tenantModules.FindAsync(tenantId, ct))?.IsEnabled(TenantModuleKeys.Wms) == true;

    // §5.6: fuera de la ventana de 30 días del ledger se usa la hora del proceso, y se registra. Un minuto de margen: el ledger evalúa
    // la ventana con su propio reloj, unos milisegundos después de éste.
    private DateTimeOffset ResolveOccurredAt(ParsedInventoryEvent evt, DateTimeOffset now)
    {
        var occurredAt = evt.OccurredAt.ToUniversalTime();
        if (occurredAt < now.AddDays(-LedgerEntryRules.MaxPastDays).AddMinutes(1)
            || occurredAt > now.AddMinutes(LedgerEntryRules.MaxFutureMinutes - 1))
        {
            telemetry.OccurredAtOutOfWindow(evt.TenantId, evt.DocumentType, evt.DocumentId, evt.OccurredAt);
            return now;
        }

        return occurredAt;
    }

    private async Task ReserveAsync(ParsedInventoryEvent evt, DocumentStockState state, CancellationToken ct)
    {
        var (warehouseId, locationId) = await provisioner.EnsureAsync(evt.TenantId, ct);
        var ownerId = (await owners.GetSelfAsync(evt.TenantId, ct)).Id.Value;
        var (movable, skipped) = await SplitAsync(evt, ct);

        await OpenAsync(evt, ownerId, warehouseId, movable, ct);
        state.RecordPosting(warehouseId, locationId, null, null);
        state.SetSkippedLines(skipped.Select(line => line.Line.LineId));
    }

    private async Task ReplaceReservationAsync(ParsedInventoryEvent evt, DocumentStockState state, CancellationToken ct)
    {
        var (warehouseId, locationId) = await WarehouseOfAsync(evt, state, ct);
        var ownerId = (await owners.GetSelfAsync(evt.TenantId, ct)).Id.Value;
        var (movable, skipped) = await SplitAsync(evt, ct);

        await reservations.CloseAllAsync(evt.TenantId, evt.DocumentType, evt.DocumentId, ct);
        await OpenAsync(evt, ownerId, warehouseId, movable, ct);
        state.RecordPosting(warehouseId, locationId, state.PostingBatchId, state.PostingOccurredAt);
        state.SetSkippedLines(skipped.Select(line => line.Line.LineId));
    }

    private async Task TakeOutAsync(ParsedInventoryEvent evt, DocumentStockState state, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var (warehouseId, locationId) = await WarehouseOfAsync(evt, state, ct);
        var ownerId = (await owners.GetSelfAsync(evt.TenantId, ct)).Id.Value;
        var (movable, skipped) = await SplitAsync(evt, ct);

        await reservations.ConsumeAllAsync(evt.TenantId, evt.DocumentType, evt.DocumentId, ct);
        var batchId = await PostAsync(
            evt, ownerId, warehouseId, locationId, movable, occurredAt, MovementReason.Ship, LocationRole.Customer, towardsGeneral: false, ct);
        state.RecordPosting(warehouseId, locationId, batchId, batchId is null ? null : occurredAt);
        state.SetSkippedLines(skipped.Select(line => line.Line.LineId));
    }

    // Recibir (compra) y vender (POS): siempre en la bodega principal de ahora; lo que se deshaga después vuelve a ésta, no a la principal
    // de entonces.
    private async Task PostFromPrincipalAsync(
        ParsedInventoryEvent evt,
        DocumentStockState state,
        DateTimeOffset occurredAt,
        MovementReason reason,
        LocationRole virtualRole,
        bool towardsGeneral,
        CancellationToken ct)
    {
        var (warehouseId, locationId) = await provisioner.EnsureAsync(evt.TenantId, ct);
        var ownerId = (await owners.GetSelfAsync(evt.TenantId, ct)).Id.Value;
        var (movable, skipped) = await SplitAsync(evt, ct);

        var batchId = await PostAsync(evt, ownerId, warehouseId, locationId, movable, occurredAt, reason, virtualRole, towardsGeneral, ct);
        state.RecordPosting(warehouseId, locationId, batchId, batchId is null ? null : occurredAt);
        state.SetSkippedLines(skipped.Select(line => line.Line.LineId));
    }

    private async Task CompensateAsync(
        ParsedInventoryEvent evt, DocumentStockState state, DateTimeOffset occurredAt, bool reserveAgain, CancellationToken ct)
    {
        var (warehouseId, locationId) = await WarehouseOfAsync(evt, state, ct);
        IReadOnlyList<StockMovement> original = [];
        if (state.PostingBatchId is { } batchId && state.PostingOccurredAt is { } postedAt)
        {
            original = await movements.ListBatchAsync(evt.TenantId, batchId, postedAt, ct);
            if (original.Count > 0)
            {
                var entries = original
                    .Select(movement => LedgerEntry.Reverse(ToEntry(movement), movement.Id, movement.OccurredAt, occurredAt, null))
                    .ToArray();
                // La compensación comparte el lote del original, como la reversa manual.
                await ledger.PostAsync(
                    new LedgerBatch(evt.TenantId, batchId, IdempotencyKey(evt, "reversal"), SystemActor, entries), ct);
            }
        }

        if (reserveAgain)
        {
            foreach (var movement in original)
            {
                await reservations.OpenAsync(
                    new DemandLine(
                        evt.TenantId,
                        movement.OwnerId,
                        movement.VariantId,
                        movement.Quantity,
                        evt.DocumentType,
                        evt.DocumentId,
                        movement.DocumentLineId ?? throw new InvalidOperationException("An inventory movement always carries its line.")),
                    warehouseId,
                    ct);
            }
        }

        state.RecordPosting(warehouseId, locationId, null, null);
    }

    private async Task<Guid?> PostAsync(
        ParsedInventoryEvent evt,
        Guid ownerId,
        Guid warehouseId,
        Guid generalId,
        IReadOnlyList<InventoryEventLine> movable,
        DateTimeOffset occurredAt,
        MovementReason reason,
        LocationRole virtualRole,
        bool towardsGeneral,
        CancellationToken ct)
    {
        if (movable.Count == 0)
        {
            return null;
        }

        var virtualLocation = await warehouses.GetVirtualLocationAsync(evt.TenantId, new WarehouseId(warehouseId), virtualRole, ct)
            ?? throw new InvalidOperationException($"Warehouse '{warehouseId}' has no {virtualRole} location.");
        var entries = movable
            .OrderBy(line => line.VariantId)
            .ThenBy(line => line.LineId)
            .Select(line =>
            {
                var variantId = line.VariantId!.Value;
                var general = new StockKey(ownerId, generalId, variantId, null, StockStatus.Available);
                var other = new StockKey(ownerId, virtualLocation.Id.Value, variantId, null, StockStatus.Available);
                return new LedgerEntry(
                    towardsGeneral ? other : general,
                    towardsGeneral ? general : other,
                    line.Quantity,
                    null,
                    reason,
                    new LedgerDocument(evt.DocumentType, evt.DocumentId, line.LineId),
                    occurredAt);
            })
            .ToArray();

        var batchId = Guid.CreateVersion7();
        await ledger.PostAsync(new LedgerBatch(evt.TenantId, batchId, IdempotencyKey(evt, reason.ToString()), SystemActor, entries), ct);
        return batchId;
    }

    private async Task OpenAsync(
        ParsedInventoryEvent evt, Guid ownerId, Guid warehouseId, IReadOnlyList<InventoryEventLine> movable, CancellationToken ct)
    {
        foreach (var line in movable.OrderBy(line => line.VariantId).ThenBy(line => line.LineId))
        {
            await reservations.OpenAsync(
                new DemandLine(evt.TenantId, ownerId, line.VariantId!.Value, line.Quantity, evt.DocumentType, evt.DocumentId, line.LineId),
                warehouseId,
                ct);
        }
    }

    // Una fila que ya tiene bodega la conserva (la reversa vuelve a donde fue el original); sólo una sin ella (todas sus líneas se
    // apartaron) toma la principal de ahora.
    private async Task<(Guid WarehouseId, Guid LocationId)> WarehouseOfAsync(
        ParsedInventoryEvent evt, DocumentStockState state, CancellationToken ct) =>
        state.WarehouseId is { } warehouseId && state.LocationId is { } locationId
            ? (warehouseId, locationId)
            : await provisioner.EnsureAsync(evt.TenantId, ct);

    private async Task<(IReadOnlyList<InventoryEventLine> Movable, IReadOnlyList<SkippedLine> Skipped)> SplitAsync(
        ParsedInventoryEvent evt, CancellationToken ct)
    {
        var variantIds = evt.Lines.Where(line => line.VariantId is not null).Select(line => line.VariantId!.Value).Distinct().ToArray();
        var snapshots = variantIds.Length == 0
            ? []
            : (await catalog.GetVariantsAsync(evt.TenantId, variantIds, ct)).ToDictionary(snapshot => snapshot.VariantId);
        var found = variantIds.Length == 0
            ? []
            : (await profiles.ListAsync(evt.TenantId, variantIds, ct)).ToDictionary(profile => profile.VariantId);

        var (movable, skipped) = InventoryLineFilter.Split(evt.Lines, snapshots, found);
        foreach (var line in skipped)
        {
            telemetry.LineSkipped(evt.TenantId, evt.DocumentType, evt.DocumentId, line.Line.LineId, line.Reason);
        }

        return (movable, skipped);
    }

    private static string IdempotencyKey(ParsedInventoryEvent evt, string what) =>
        $"inventory:{evt.DocumentType}:{evt.DocumentId:N}:{what}:v{evt.Version}";

    // La entrada original a partir de su fila: lo que el ledger escribió es lo que el compensatorio invierte.
    private static LedgerEntry ToEntry(StockMovement movement) =>
        new(
            new StockKey(movement.OwnerId, movement.FromLocationId, movement.VariantId, movement.LotId, movement.FromStatus, movement.FromHandlingUnitId),
            new StockKey(movement.OwnerId, movement.ToLocationId, movement.VariantId, movement.LotId, movement.ToStatus, movement.ToHandlingUnitId),
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
}
