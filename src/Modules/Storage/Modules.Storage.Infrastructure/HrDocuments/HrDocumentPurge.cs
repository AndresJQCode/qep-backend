using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.HrDocuments;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Por cada id»): purga los PDF que Hr soltó y que ya no usa. Un borrado equivocado no se
/// recupera (la fila queda <c>Purged</c> y el objeto ya no está en R2), así que todo falla cerrado:
/// <list type="bullet">
/// <item>Sólo un <see cref="FileOwnerType.HrDocument"/> del tenant del aviso, <c>Available</c> o ya marcado (<c>Deleted</c>, que para un
/// <c>HrDocument</c> sólo significa la marca de esta purga); cualquier otro id se salta (el de otro dueño se registra).</item>
/// <item>La primera pregunta a <see cref="IHrDocumentReferences"/> va bajo el candado de la fila; si Hr lo usa, se queda sin marcar. Si
/// nadie lo usa, se marca <c>Deleted</c> y se <b>confirma</b>: el candado no frena al adjunto de Hr, que lee sin él, pero desde ese commit
/// un adjunto que lea Storage recibe <c>hr.file.not_found</c>. La segunda pregunta va con la marca ya confirmada, bajo el candado de una
/// transacción nueva; si Hr lo usa, el archivo sólo vuelve a <c>Available</c> si <c>StatAsync</c> encuentra el objeto en R2. Es una
/// dependencia obligatoria del constructor (no una lista de sondas): sin el adaptador el contenedor no se arma.</item>
/// <item>Primero R2 y después la base, siempre con la marca confirmada: un guardado fallido tras borrar en R2 deja la fila <c>Deleted</c>,
/// nunca <c>Available</c> sin objeto, y el reintento retoma en la segunda pregunta. Una excepción (R2, la pregunta o la base) revierte la
/// transacción en curso y sube: el mensaje no se marca y vuelve cuando vence su reclamo.</item>
/// <item>Ventana residual (spec, aceptada por el owner): un adjunto que leyó el archivo <c>Available</c> antes del commit de la marca y que
/// confirma su registro de Hr después de la pregunta que decidió borrar queda nombrando un PDF purgado. Si esa decisión cae en un reintento
/// (el objeto ya no está), se ve en el log como <c>LogPurgedWhileReferenced</c>, en <c>Error</c>.</item>
/// </list>
/// Regla R1 del plan: cada archivo cierra sus transacciones (dos si se marca), también el que se salta, para que los candados no se
/// acumulen; el inbox se marca en el último guardado, aunque el último id se salte o la lista venga vacía.
/// </summary>
internal sealed partial class HrDocumentPurge(
    IHrDocumentPurgeStore store,
    IHrDocumentReferences references,
    IObjectStorage objectStorage,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    ILogger<HrDocumentPurge> logger)
{
    internal const string Reason = "hr_document_released";

    [LoggerMessage(Level = LogLevel.Warning, Message = "HR document release names file {FileId}, owned by {OwnerType}; only HR documents are purged, so it is skipped.")]
    private static partial void LogNotAnHrDocument(ILogger logger, Guid fileId, FileOwnerType ownerType);

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} does not exist in tenant {TenantId}; it is skipped.")]
    private static partial void LogMissing(ILogger logger, Guid fileId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} is {Status}, neither Available nor marked for purge; it is skipped.")]
    private static partial void LogNotAvailable(ILogger logger, Guid fileId, FileResourceStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} marked for purge.")]
    private static partial void LogMarked(ILogger logger, Guid fileId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HR document {FileId} was attached again after it was marked for purge; it is available again.")]
    private static partial void LogRestored(ILogger logger, Guid fileId);

    [LoggerMessage(Level = LogLevel.Error, Message = "HR document {FileId} of tenant {TenantId} is used by Hr but its object was already deleted from R2; it is purged.")]
    private static partial void LogPurgedWhileReferenced(ILogger logger, Guid fileId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} is still used by Hr; it is kept.")]
    private static partial void LogKept(ILogger logger, Guid fileId);

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} purged.")]
    private static partial void LogPurged(ILogger logger, Guid fileId);

    public async Task PurgeAsync(Guid tenantId, IReadOnlyList<Guid> fileIds, Func<Task> markProcessed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(markProcessed);

        // La fecha es la de la purga, no la del aviso.
        var now = clock.UtcNow;
        try
        {
            if (fileIds.Count == 0)
            {
                await markProcessed();
                await store.SaveAsync(ct);
                return;
            }

            for (var index = 0; index < fileIds.Count; index++)
            {
                await PurgeOneAsync(tenantId, fileIds[index], now, ct);
                if (index == fileIds.Count - 1)
                {
                    await markProcessed();
                }

                await store.SaveAsync(ct);
            }
        }
        catch
        {
            await store.AbandonAsync();
            throw;
        }
    }

    private async Task PurgeOneAsync(Guid tenantId, Guid fileId, DateTimeOffset now, CancellationToken ct)
    {
        var resource = await store.LockAsync(tenantId, new FileResourceId(fileId), ct);
        if (resource is null)
        {
            LogMissing(logger, fileId, tenantId);
            return;
        }

        if (resource.OwnerType is not FileOwnerType.HrDocument)
        {
            LogNotAnHrDocument(logger, fileId, resource.OwnerType);
            return;
        }

        if (resource.Status is not (FileResourceStatus.Available or FileResourceStatus.Deleted))
        {
            LogNotAvailable(logger, fileId, resource.Status);
            return;
        }

        if (resource.Status is FileResourceStatus.Available)
        {
            // Paso 2, la primera pregunta, bajo el candado: si Hr lo usa, se queda sin marcar.
            if ((await references.FindReferencedAsync(tenantId, [fileId], ct)).Contains(fileId))
            {
                LogKept(logger, fileId);
                return;
            }

            // Paso 3, la marca confirmada: desde este commit, un adjunto que lea Storage no lo encuentra.
            resource.BeginHrDocumentPurge(now);
            LogMarked(logger, fileId);
            await store.SaveAsync(ct);

            // Otra transacción y otro candado: si otra réplica lo terminó o lo devolvió entretanto, se salta.
            resource = await store.LockAsync(tenantId, new FileResourceId(fileId), ct);
            if (resource is null)
            {
                LogMissing(logger, fileId, tenantId);
                return;
            }

            if (resource.Status is not FileResourceStatus.Deleted)
            {
                LogNotAvailable(logger, fileId, resource.Status);
                return;
            }
        }

        // Paso 4, la segunda pregunta, con la marca ya confirmada (o la de un intento anterior). La retiene sólo un registro de Hr confirmado
        // antes de esta pregunta; uno que se confirme después, si leyó el archivo antes de la marca, es la ventana residual del spec.
        if (!(await references.FindReferencedAsync(tenantId, [fileId], ct)).Contains(fileId))
        {
            // El objeto principal antes que las variantes: si un corte deja algo, StatAsync lo ve en el reintento.
            await objectStorage.DeleteAsync(resource.StorageKey, ct);
            foreach (var variant in resource.Variants)
            {
                await objectStorage.DeleteAsync(variant.StorageKey, ct);
            }

            resource.PurgeReleasedHrDocument(now);
            auditPublisher.PublishSystem(tenantId, "storage.file.purged", "file", resource.Id.ToString(), Reason, now);
            LogPurged(logger, fileId);
            return;
        }

        // Un adjunto se cruzó con la marca. Sólo vuelve a Available si el objeto sigue en R2: nunca una fila Available sin objeto.
        if (await objectStorage.StatAsync(resource.StorageKey, ct) is not null)
        {
            resource.RestoreHrDocument(now);
            LogRestored(logger, fileId);
            return;
        }

        // Un intento anterior ya lo borró de R2 y no alcanzó a guardar: devolverlo dejaría una fila sin objeto. Es el riesgo residual.
        resource.PurgeReleasedHrDocument(now);
        auditPublisher.PublishSystem(tenantId, "storage.file.purged", "file", resource.Id.ToString(), Reason, now);
        LogPurgedWhileReferenced(logger, fileId, tenantId);
    }
}
