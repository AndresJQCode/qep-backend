using System.Globalization;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;
using Modules.Tenancy.Application;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>
/// Lo que pasa cuando un <c>Count</c> se cierra (spec 2026-11-26 §8.4), dentro de la transacción del cierre y por el mismo
/// <see cref="TaskClosedNotifier"/> que llaman el operario y el supervisor. Con el conteo <c>Done</c>, compara lo contado con el
/// saldo de esa clave al momento de contar: sin diferencia, el sistema resuelve las novedades que verificaba; con diferencia, propone
/// un ajuste <c>CountVariance</c> (nunca toca el ledger: el movimiento lo hace el ajuste una vez aprobado) y deja una novedad
/// <c>CountVariance</c> que lo sigue. Con el conteo <c>Cancelled</c>, desenlaza esas novedades y quedan abiertas. <b>Nunca toma el
/// <c>count-key</c></b>: ya tiene la fila del <c>Count</c> (orden de bloqueos); numera ajuste y novedad al final.
/// </summary>
public sealed class CountVerificationHook(
    IIncidentRepository incidents,
    IStockReadQueries stock,
    IAdjustmentRepository adjustments,
    IWmsAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    WmsNumberingDay numberingDay)
    : ITaskClosedHook
{
    public async Task OnClosedAsync(TaskClosed closed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(closed);
        var task = closed.Task;
        if (task.Type != WarehouseTaskType.Count)
        {
            return;
        }

        var verified = await incidents.ListOpenVerifiedByAsync(task.TenantId, task.Id, cancellationToken);
        switch (task.Status)
        {
            case WarehouseTaskStatus.Cancelled:
                // Nadie contó: las novedades siguen abiertas para que el supervisor las acepte, y la próxima sobre esa clave
                // genera un conteo nuevo.
                foreach (var incident in verified)
                {
                    incident.UnlinkVerification(task.Number, closed.Now);
                }

                return;
            case WarehouseTaskStatus.Done:
                foreach (var line in task.Lines.Where(line => line.Status == TaskLineStatus.Done && line.QuantityCounted is not null))
                {
                    await EvaluateAsync(closed, line, verified, cancellationToken);
                }

                return;
            default:
                return;
        }
    }

    private async Task EvaluateAsync(
        TaskClosed closed, WarehouseTaskLine line, IReadOnlyList<Incident> verified, CancellationToken cancellationToken)
    {
        var task = closed.Task;
        var counted = line.QuantityCounted!.Value;
        var key = new StockKey(
            task.OwnerId.Value, line.FromLocationId.Value, line.VariantId, line.LotId?.Value, line.StockStatus);
        // Lo esperado se calcula ahora, con el saldo de la clave, y no se guarda en la línea: el conteo es a ciegas (DA-39).
        var expected = await stock.FindOnHandAsync(task.TenantId, key, cancellationToken);
        var delta = counted - expected;

        // Las novedades que este conteo verifica sobre esta ubicación y variante (el lote lo completa el escaneo del operario).
        var mine = verified
            .Where(incident => incident.LocationId == line.FromLocationId
                && incident.VariantId == line.VariantId
                && (incident.StockStatus ?? StockStatus.Available) == line.StockStatus)
            .ToList();

        if (delta == 0)
        {
            foreach (var incident in mine)
            {
                incident.ResolveBySystem(
                    IncidentResolution.Accept, $"Verificada por {task.Number}: sin diferencia.", null, closed.Now);
            }

            return;
        }

        var adjustmentNumber = await adjustments.NextNumberAsync(
            task.TenantId, await numberingDay.OfAsync(task.TenantId, closed.Now, cancellationToken), cancellationToken);
        var adjustment = Adjustment.Propose(
            task.TenantId,
            adjustmentNumber,
            task.OwnerId,
            task.WarehouseId,
            AdjustmentReason.CountVariance,
            task.Number,
            closed.OccurredAt,
            [AdjustmentLine.Create(line.FromLocationId, line.VariantId, line.LotId, line.StockStatus, delta, null, null)],
            executionContext.SubjectId,
            closed.Now);
        adjustments.Add(adjustment);
        auditRecorder.Record(
            task.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            AdjustmentProposed,
            Resources.Adjustment,
            adjustment.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = task.OwnerId.Value.ToString(),
                ["number"] = adjustment.Number,
                ["reason"] = adjustment.Reason.ToString(),
                ["lineCount"] = adjustment.Lines.Count.ToString(CultureInfo.InvariantCulture),
                ["note"] = adjustment.Note,
                ["source"] = "count",
                ["taskNumber"] = task.Number,
            },
            closed.Now);

        var incidentNumber = await incidents.NextNumberAsync(
            task.TenantId, await numberingDay.OfAsync(task.TenantId, closed.Now, cancellationToken), cancellationToken);
        var variance = Incident.Report(
            task.TenantId,
            incidentNumber,
            task.WarehouseId,
            task.OwnerId,
            IncidentType.CountVariance,
            new IncidentSubject(
                task.Id, line.Id, line.FromLocationId, line.VariantId, line.LotId, line.StockStatus, expected, counted),
            $"Conteo {task.Number}: contó {counted.ToString("0.######", CultureInfo.InvariantCulture)} y el sistema tenía "
            + expected.ToString("0.######", CultureInfo.InvariantCulture) + ".",
            null,
            null,
            [],
            closed.ActorMemberId,
            null,
            closed.OperationKey,
            closed.OccurredAt,
            closed.Now);
        variance.AttachAdjustment(adjustment.Id);
        incidents.Add(variance);

        foreach (var incident in mine)
        {
            incident.ResolveBySystem(
                IncidentResolution.Accept, $"Verificada por {task.Number}: ver {variance.Number}.", variance.Id, closed.Now);
        }
    }
}
