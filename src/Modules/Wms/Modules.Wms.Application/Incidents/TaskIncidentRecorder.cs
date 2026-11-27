using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

/// <summary>Arma la novedad que nace de una línea de tarea (spec 2026-11-26 §8.4). No guarda: comparte la transacción del handler que
/// la llama, con la misma <c>Idempotency-Key</c>. La numeración toma un lock de transacción, así que va antes de cualquier llamada
/// al ledger (orden de bloqueos: números antes que saldos).</summary>
public sealed class TaskIncidentRecorder(IIncidentRepository incidents, IWmsEvidenceLookup evidenceLookup)
{
    /// <summary>Las fotos que el operario adjuntó, como evidencia (DA-49). Se de-duplican primero; más de
    /// <see cref="Incident.MaxEvidence"/> distintas, una de otro tenant, de otro tipo de dueño o que no existe es 422
    /// <c>wms.incident.evidence_invalid</c>. Cuentan en cualquier estado: pueden estar subiendo todavía.</summary>
    public async Task<IReadOnlyList<IncidentEvidence>> ResolveEvidenceAsync(
        Guid tenantId, IReadOnlyList<Guid>? photoFileIds, CancellationToken cancellationToken)
    {
        var distinct = (photoFileIds ?? []).Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        if (distinct.Length > Incident.MaxEvidence)
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentEvidenceInvalid, $"Una novedad lleva a lo sumo {Incident.MaxEvidence} evidencias.");
        }

        var found = await evidenceLookup.FindEvidenceAsync(tenantId, distinct, cancellationToken);
        return distinct.All(found.Contains)
            ? [.. distinct.Select(fileId => IncidentEvidence.Create(fileId, IncidentEvidenceKind.Photo, null))]
            : throw new WmsDomainException(
                WmsErrorCodes.IncidentEvidenceInvalid, "Una de las fotos no es evidencia de bodega de este tenant.");
    }

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
        IReadOnlyList<Guid>? photoFileIds,
        Guid member,
        string deviceId,
        string operationKey,
        DateTimeOffset reportedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(line);

        // La evidencia se valida antes de numerar: una foto ajena no toma el lock de numeración.
        var evidence = await ResolveEvidenceAsync(task.TenantId, photoFileIds, cancellationToken);
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
            evidence,
            member,
            deviceId,
            operationKey,
            reportedAt,
            now);
        incidents.Add(incident);
        return incident;
    }

    /// <summary>La acción que llegó con un lease perdido, tal cual llegó (DA-48), sin el <c>leaseToken</c>. Si esa
    /// <c>Idempotency-Key</c> ya dejó su novedad no agrega otra. No guarda: el runner confirma la transacción con sólo esto y después
    /// responde 409. Una línea que no es de la tarea no pone sujeto de línea: la tarea existe y lo que importa es el hecho físico.
    /// El lote que viaja en la acción queda sólo en el JSON: la FK de la novedad al lote no mira el tenant y un id cualquiera
    /// rompería el guardado.</summary>
    public async Task RecordSyncConflictAsync(
        WarehouseTask task,
        WarehouseTaskLine? line,
        Guid member,
        string deviceId,
        string operationKey,
        string action,
        object rawCommand,
        decimal? quantity,
        DateTimeOffset occurredAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(rawCommand);

        if (await incidents.ExistsWithOperationKeyAsync(task.TenantId, operationKey, cancellationToken))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToNode(rawCommand, rawCommand.GetType(), JsonSerializerOptions.Web) as JsonObject
            ?? new JsonObject();
        payload["action"] = action;
        payload.Remove("leaseToken");

        var subject = line is null
            ? new IncidentSubject(task.Id, null, null, null, null, null, null, quantity)
            : new IncidentSubject(
                task.Id, line.Id, line.FromLocationId, line.VariantId, line.LotId, line.StockStatus, null, quantity);
        var inWindow = occurredAt >= now.AddDays(-LedgerEntryRules.MaxPastDays)
            && occurredAt <= now.AddMinutes(LedgerEntryRules.MaxFutureMinutes);
        var number = await incidents.NextNumberAsync(task.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        incidents.Add(Incident.Report(
            task.TenantId,
            number,
            task.WarehouseId,
            task.OwnerId,
            IncidentType.SyncConflict,
            subject,
            $"La acción '{action}' llegó con el reclamo de la tarea perdido y no se aplicó.",
            null,
            payload.ToJsonString(),
            [],
            member,
            deviceId,
            operationKey,
            inWindow ? occurredAt : now,
            now));
    }
}
