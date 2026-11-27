using System.Globalization;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>
/// Un ajuste <c>CountVariance</c> vale mientras el saldo de su clave siga siendo el que el conteo vio (decisión del owner, 2026-11-27).
/// Si cambió (un confirm que llegó tarde de un celular sin conexión, otro ajuste ya contabilizado), al aprobar o contabilizar el
/// sistema rechaza el ajuste y su <c>CountVariance</c>, pide un conteo nuevo de la misma clave (o enlaza el que ya está abierto),
/// enlaza a él las novedades que el conteo viejo verificaba, <b>guarda</b> y responde 422 <c>wms.adjustment.count_stale</c>. Es el mismo
/// patrón de <c>SyncConflict</c> en <see cref="OperatorLineRunner"/>: se confirma antes del <c>throw</c>, y como no se escribe recibo, el
/// filtro de idempotencia no lo cubre. El rechazo se audita como cualquier rechazo, con el sistema de actor y quien lo disparó en
/// <c>triggeredBy</c>. El que llama ya abrió la transacción.
/// </summary>
public sealed class CountVarianceFreshness(
    IStockReadQueries stock,
    IWarehouseTaskRepository tasks,
    IIncidentRepository incidents,
    TaskIncidentRecorder recorder,
    IWmsAuditRecorder auditRecorder,
    IWmsUnitOfWork unitOfWork,
    IExecutionContext executionContext)
{
    /// <summary>Si el saldo sigue siendo el del conteo, vuelve; si no, rechaza, guarda y lanza. Sin bloqueos de saldo: para el camino
    /// que sólo aprueba y para la primera mirada del que contabiliza (el conteo nuevo toma <c>count-key</c> y numeraciones, que van
    /// antes que los saldos).</summary>
    public async Task EnsureFreshAsync(Adjustment adjustment, Incident variance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentNullException.ThrowIfNull(variance);
        if (variance.ExpectedQty is not { } expected || adjustment.Lines.Count != 1)
        {
            return;
        }

        var key = KeyOf(adjustment);
        var current = await stock.FindOnHandAsync(adjustment.TenantId, key, cancellationToken);
        if (current != expected)
        {
            await RejectAndRecountAsync(adjustment, variance, expected, current, now, cancellationToken);
        }
    }

    /// <summary>Después de <see cref="EnsureFreshAsync"/> y antes del ledger: bloquea los saldos que el ajuste va a tocar, en el orden
    /// del ledger, y vuelve a leer. Si en el medio se movieron, deshace (412): ya con los saldos bloqueados no se puede tomar
    /// <c>count-key</c>, así que el reintento pasa por <see cref="EnsureFreshAsync"/> y lo rechaza limpio.</summary>
    public async Task LockBalancesAsync(
        Adjustment adjustment,
        Incident variance,
        IReadOnlyCollection<StockKey> keysToLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentNullException.ThrowIfNull(variance);
        ArgumentNullException.ThrowIfNull(keysToLock);
        if (variance.ExpectedQty is not { } expected || adjustment.Lines.Count != 1)
        {
            return;
        }

        await stock.LockBalancesAsync(adjustment.TenantId, keysToLock, cancellationToken);
        if (await stock.FindOnHandAsync(adjustment.TenantId, KeyOf(adjustment), cancellationToken) != expected)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "El saldo cambió mientras se contabilizaba el ajuste. Intenta de nuevo.");
        }
    }

    private static StockKey KeyOf(Adjustment adjustment)
    {
        var line = adjustment.Lines[0];
        return new StockKey(adjustment.OwnerId.Value, line.LocationId.Value, line.VariantId, line.LotId?.Value, line.Status);
    }

    private async Task RejectAndRecountAsync(
        Adjustment adjustment, Incident variance, decimal expected, decimal current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sourceNumber = variance.SourceTaskId is { } sourceId
            && (await tasks.FindNumbersAsync(adjustment.TenantId, [sourceId], cancellationToken)).TryGetValue(sourceId, out var found)
                ? found
                : "anterior";
        var note = $"El saldo cambió desde el conteo {sourceNumber}: había {Format(expected)} y ahora hay {Format(current)}. "
            + "Se pidió un conteo nuevo.";

        // Orden de bloqueos: count-key → el Count abierto → task-seq. Sin saldos bloqueados todavía.
        var line = adjustment.Lines[0];
        var target = await recorder.LockCountTargetAsync(
            adjustment.TenantId,
            adjustment.WarehouseId.Value,
            adjustment.OwnerId.Value,
            line.LocationId.Value,
            line.VariantId,
            line.LotId?.Value,
            line.Status,
            cancellationToken);

        adjustment.Reject(Guid.Empty, note, now);
        variance.RejectByAdjustment(Guid.Empty, note, now);

        string? recountNumber = null;
        if (target is not null)
        {
            var (recountId, number) = await recorder.OpenRecountAsync(variance, target, now, cancellationToken);
            recountNumber = number;
            // Las novedades del conteo viejo, abiertas o ya cerradas, pasan al nuevo: la separación de funciones se deriva de ellas.
            if (variance.SourceTaskId is { } oldCount)
            {
                foreach (var verified in await incidents.ListVerifiedByAsync(adjustment.TenantId, oldCount, cancellationToken))
                {
                    verified.RelinkVerification(recountId, number, now);
                }
            }
        }

        var changed = new Dictionary<string, string?>
        {
            ["ownerId"] = adjustment.OwnerId.Value.ToString(),
            ["number"] = adjustment.Number,
            ["note"] = adjustment.RejectNote,
            ["reason"] = "count_stale",
            ["triggeredBy"] = executionContext.SubjectId.ToString(),
        };
        if (recountNumber is not null)
        {
            changed["recountTask"] = recountNumber;
        }

        auditRecorder.Record(
            adjustment.TenantId, Guid.Empty, WmsActorType.System, AdjustmentRejected, Resources.Adjustment, adjustment.Id.ToString(), changed, now);

        // Se confirma antes del throw (como SyncConflict): sin esto el await using deshace el rechazo y el conteo nuevo.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        throw new WmsDomainException(WmsErrorCodes.AdjustmentCountStale, note);
    }

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
