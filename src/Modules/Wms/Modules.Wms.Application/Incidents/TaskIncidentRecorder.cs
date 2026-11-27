using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

/// <summary>Arma la novedad que nace de una línea de tarea (spec 2026-11-26 §8.4). No guarda: comparte la transacción del handler que
/// la llama, con la misma <c>Idempotency-Key</c>. La numeración toma un lock de transacción, así que va antes de cualquier llamada
/// al ledger (orden de bloqueos: números antes que saldos).</summary>
public sealed class TaskIncidentRecorder(IIncidentRepository incidents)
{
    /// <param name="expected">Lo que faltaba de la línea.</param>
    /// <param name="actual">Lo que el operario encontró.</param>
    /// <param name="reportedAt">La hora del escaneo.</param>
    /// <param name="now">La hora del servidor.</param>
    public async Task<Incident> ReportFromLineAsync(
        WarehouseTask task,
        WarehouseTaskLine line,
        IncidentType type,
        decimal expected,
        decimal actual,
        LotId? lot,
        string? note,
        Guid member,
        string deviceId,
        string operationKey,
        DateTimeOffset reportedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(line);

        var number = await incidents.NextNumberAsync(task.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var subject = new IncidentSubject(
            task.Id, line.Id, line.FromLocationId, line.VariantId, lot, line.StockStatus, expected, actual);
        var incident = Incident.Report(
            task.TenantId,
            number,
            task.WarehouseId,
            task.OwnerId,
            type,
            subject,
            note,
            null,
            null,
            [],
            member,
            deviceId,
            operationKey,
            reportedAt,
            now);
        incidents.Add(incident);
        return incident;
    }
}
