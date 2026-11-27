using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.HrDocuments;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Por cada id»): purga los PDF que Hr soltó y que ya no usa. Un borrado equivocado no se
/// recupera (la fila queda <c>Purged</c> y el objeto ya no está en R2), así que todo falla cerrado:
/// <list type="bullet">
/// <item>Sólo un <see cref="FileOwnerType.HrDocument"/> <c>Available</c> del tenant del aviso; cualquier otro id se salta (el de otro dueño
/// se registra).</item>
/// <item>Bajo el candado de la fila y justo antes de borrar se vuelve a preguntar a <see cref="IHrDocumentReferences"/>; si Hr lo usa, se
/// queda. Es una dependencia obligatoria del constructor (no una lista de sondas): sin el adaptador el contenedor no se arma.</item>
/// <item>Primero R2 y después la base, como <c>ProductImageReleaseProcessor</c>: borrar una clave que no existe no falla, así que un
/// guardado fallido sólo repite el borrado. Una excepción (R2, la pregunta o la base) revierte y sube: el mensaje no se marca y vuelve
/// cuando vence su reclamo.</item>
/// </list>
/// Regla R1 del plan: cada archivo cierra su propia transacción, también el que se salta, para que los candados no se acumulen; el inbox
/// se marca en el último guardado, aunque el último id se salte o la lista venga vacía.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "HR document {FileId} is {Status}, not Available; it is skipped.")]
    private static partial void LogNotAvailable(ILogger logger, Guid fileId, FileResourceStatus status);

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

        if (resource.Status is not FileResourceStatus.Available)
        {
            LogNotAvailable(logger, fileId, resource.Status);
            return;
        }

        // La segunda pregunta, bajo el candado: un registro que adjuntó el mismo PDF después del aviso lo retiene.
        var used = await references.FindReferencedAsync(tenantId, [fileId], ct);
        if (used.Contains(fileId))
        {
            LogKept(logger, fileId);
            return;
        }

        await objectStorage.DeleteAsync(resource.StorageKey, ct);
        foreach (var variant in resource.Variants)
        {
            await objectStorage.DeleteAsync(variant.StorageKey, ct);
        }

        resource.BeginHrDocumentPurge(now);
        resource.PurgeReleasedHrDocument(now);
        auditPublisher.PublishSystem(tenantId, "storage.file.purged", "file", resource.Id.ToString(), Reason, now);
        LogPurged(logger, fileId);
    }
}
