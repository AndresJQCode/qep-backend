using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Arma la novedad que nace de una línea de tarea (spec 2026-11-26 §8.4). No guarda: comparte la transacción del handler que
/// la llama, con la misma <c>Idempotency-Key</c>. La numeración toma un lock de transacción, así que va antes de cualquier llamada
/// al ledger (orden de bloqueos: números antes que saldos).</summary>
public sealed class TaskIncidentRecorder(
    IIncidentRepository incidents,
    IWmsEvidenceLookup evidenceLookup,
    IVariantProfileRepository profiles,
    IWarehouseTaskRepository tasks,
    IWmsUnitOfWork unitOfWork,
    WmsNumberingDay numberingDay)
{
    /// <summary>La clave de stock que un <c>Count</c> de verificación cuenta y, si ya hay uno abierto sobre ella, ese <c>Count</c>
    /// ya bloqueado (con su número, para la historia de la novedad).</summary>
    public sealed record VerificationTarget(
        Guid WarehouseId,
        Guid OwnerId,
        Guid LocationId,
        Guid VariantId,
        Guid? LotId,
        StockStatus Status,
        Guid? OpenCountId,
        string? OpenCountNumber);

    /// <summary>Primera mitad de la verificación (DA-44), que va <b>antes de numerar la novedad</b>: si la novedad pide un conteo
    /// ciego de su ubicación y la variante no es de series (a una de series no se le genera <c>Count</c>: el supervisor acepta),
    /// toma el <c>count-key</c> de la clave, busca el <c>Count</c> abierto sobre ella y, si hay uno, lo bloquea y confirma que sigue
    /// abierto. Devuelve <c>null</c> si no hay nada que verificar. El orden de bloqueos es operario → su tarea → <c>count-key</c> →
    /// el <c>Count</c> abierto → numeraciones → saldos: la acción <c>count</c> ya tiene la fila del <c>Count</c> y luego numera
    /// (ajuste y novedad), así que quien enlaza tiene que pedir esa fila <b>antes</b> de tomar <c>incident-seq</c>.</summary>
    public async Task<VerificationTarget?> LockVerificationAsync(
        IncidentType type,
        IncidentSubject subject,
        Guid tenantId,
        Guid warehouseId,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!IncidentCatalog.RequestsVerification(type) || subject.Location is not { } location || subject.VariantId is not { } variant)
        {
            return null;
        }

        // Una novedad suelta no trae estado: lo que se verifica es lo disponible.
        return await LockCountTargetAsync(
            tenantId, warehouseId, ownerId, location.Value, variant, subject.LotId?.Value, subject.StockStatus ?? StockStatus.Available,
            cancellationToken);
    }

    /// <summary>La mitad de <see cref="LockVerificationAsync"/> que no mira el tipo de la novedad: toma el <c>count-key</c> de la clave y
    /// bloquea el <c>Count</c> abierto sobre ella, si lo hay. <c>null</c> si la variante es de series (no se genera <c>Count</c>). Lo
    /// llama también el conteo nuevo que se pide cuando el saldo cambió desde un conteo (ver <see cref="CountVarianceFreshness"/>).</summary>
    public async Task<VerificationTarget?> LockCountTargetAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid ownerId,
        Guid location,
        Guid variant,
        Guid? lot,
        StockStatus status,
        CancellationToken cancellationToken)
    {
        var profile = await profiles.FindAsync(tenantId, variant, cancellationToken);
        if (profile?.Tracking == TrackingPolicy.Serial)
        {
            return null;
        }

        await unitOfWork.LockAsync(
            $"count-key:{tenantId:D}:{ownerId:D}:{location:D}:{variant:D}:{lot?.ToString("D") ?? "none"}:{status}",
            cancellationToken);

        Guid? openId = null;
        string? openNumber = null;
        if (await tasks.FindOpenCountAsync(tenantId, warehouseId, ownerId, location, variant, lot, status, cancellationToken)
            is { } candidate
            && await tasks.LockAsync(tenantId, candidate, cancellationToken))
        {
            // Con la fila bloqueada se vuelve a mirar: si el conteo se cerró entre la búsqueda y el bloqueo, se crea otro.
            var open = await tasks.GetAsync(tenantId, candidate, cancellationToken);
            if (open is { Status: not (WarehouseTaskStatus.Done or WarehouseTaskStatus.Cancelled) })
            {
                (openId, openNumber) = (open.Id, open.Number);
            }
        }

        return new VerificationTarget(warehouseId, ownerId, location, variant, lot, status, openId, openNumber);
    }

    /// <summary>El <c>Count</c> que sustituye a uno cuyo ajuste quedó viejo: el que ya estaba abierto sobre la clave, o uno nuevo de una
    /// línea, publicado, del sistema y con <paramref name="source"/> (la <c>CountVariance</c> rechazada) como origen. Devuelve su id y
    /// su número. El número de la tarea toma <c>task-seq</c>.</summary>
    public async Task<(Guid TaskId, string Number)> OpenRecountAsync(
        Incident source, VerificationTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (target.OpenCountId is { } openId)
        {
            return (openId, target.OpenCountNumber ?? string.Empty);
        }

        var number = await tasks.NextNumberAsync(source.TenantId, await numberingDay.OfAsync(source.TenantId, now, cancellationToken), cancellationToken);
        var count = WarehouseTask.CreateCount(
            source.TenantId,
            number,
            new WarehouseId(target.WarehouseId),
            new OwnerId(target.OwnerId),
            [WarehouseTaskLine.CreateCount(
                target.VariantId,
                target.LotId is { } lot ? new LotId(lot) : null,
                target.Status,
                new LocationId(target.LocationId))],
            TaskSourceType.Incident,
            source.Id,
            now);
        tasks.Add(count);
        return (count.Id, count.Number);
    }

    /// <summary>Segunda mitad: con la novedad ya armada, la enlaza al <c>Count</c> abierto o genera uno nuevo, de una línea, publicado,
    /// del sistema y con la novedad como origen. El número de la tarea toma <c>task-seq</c>, después de <c>incident-seq</c>.</summary>
    public async Task LinkVerificationAsync(
        Incident incident, VerificationTarget? target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        if (target is null)
        {
            return;
        }

        if (target.OpenCountId is { } openId)
        {
            incident.LinkVerification(openId, now, target.OpenCountNumber);
            return;
        }

        var number = await tasks.NextNumberAsync(incident.TenantId, await numberingDay.OfAsync(incident.TenantId, now, cancellationToken), cancellationToken);
        var count = WarehouseTask.CreateCount(
            incident.TenantId,
            number,
            new WarehouseId(target.WarehouseId),
            new OwnerId(target.OwnerId),
            [WarehouseTaskLine.CreateCount(
                target.VariantId,
                target.LotId is { } lot ? new LotId(lot) : null,
                target.Status,
                new LocationId(target.LocationId))],
            TaskSourceType.Incident,
            incident.Id,
            now);
        tasks.Add(count);
        incident.LinkVerification(count.Id, now, count.Number);
    }

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
        var subject = new IncidentSubject(
            task.Id, line.Id, line.FromLocationId, line.VariantId, lot, line.StockStatus, expected, actual);
        // Antes de numerar: el count-key y el Count abierto van antes que incident-seq (ver LockVerificationAsync).
        var verification = await LockVerificationAsync(
            type, subject, task.TenantId, task.WarehouseId.Value, task.OwnerId.Value, cancellationToken);
        var number = await incidents.NextNumberAsync(task.TenantId, await numberingDay.OfAsync(task.TenantId, now, cancellationToken), cancellationToken);
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
        await LinkVerificationAsync(incident, verification, now, cancellationToken);
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
        var number = await incidents.NextNumberAsync(task.TenantId, await numberingDay.OfAsync(task.TenantId, now, cancellationToken), cancellationToken);
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
