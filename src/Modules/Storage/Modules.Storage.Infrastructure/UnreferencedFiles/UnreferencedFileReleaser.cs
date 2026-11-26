using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.UnreferencedFiles;

/// <summary>
/// Suelta un archivo que ya nadie usa: lo reclama bajo candado, vuelve a preguntar si lo usan, retira sus copias públicas, lo borra lógico,
/// audita <c>storage.file.deleted</c> como el sistema con el motivo y confirma. Lo comparten los barridos diarios
/// (<see cref="UnreferencedFileSweep"/>) y el consumidor del aviso de un chatbot borrado.
/// </summary>
/// <remarks>
/// Devuelve <c>false</c> si no hubo nada que soltar (otra réplica lo tiene o ya lo soltó, ya no es de ese dueño, o lo usan otra vez) y
/// <c>true</c> si lo soltó. Un fallo (R2, la base, la pregunta de uso) deshace el reclamo y se propaga: el barrido lo registra y sigue, y el
/// consumidor deja el mensaje sin marcar para el tick siguiente.
/// </remarks>
internal sealed partial class UnreferencedFileReleaser(
    IUnreferencedFileCandidates files,
    FilePublication filePublication,
    IStorageAuditPublisher auditPublisher,
    ILogger<UnreferencedFileReleaser> logger)
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not roll back the claim of file {FileId}; the lock is released when the connection closes.")]
    private static partial void LogAbandonFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Released {OwnerType} file {FileId} of tenant {TenantId} ({Reason}).")]
    private static partial void LogReleased(ILogger logger, FileOwnerType ownerType, Guid fileId, Guid tenantId, string reason);

    public async Task<bool> ReleaseAsync(
        FileOwnerType ownerType,
        Guid tenantId,
        FileResourceId id,
        string reason,
        Func<Task<bool>> stillReferenced,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            // Bajo candado y releído: otra réplica que lo tenga o ya lo haya soltado lo deja fuera, sin auditar dos veces.
            var resource = await files.ClaimAsync(ownerType, tenantId, id, cancellationToken);
            if (resource is null)
            {
                return false;
            }

            // Revisión final: entre la pregunta de la página (o del aviso) y el reclamo, alguien pudo volver a usar el archivo. Se pregunta
            // otra vez por este archivo bajo el candado; si lo usan, se suelta el candado sin tocarlo. Un fallo de la pregunta cae al catch:
            // falla cerrado y no se suelta.
            if (await stillReferenced())
            {
                await AbandonQuietlyAsync(id);
                return false;
            }

            // Primero las copias públicas y después el guardado: borrar una clave que no existe no falla, así que un guardado fallido sólo
            // repite el retiro la próxima vez. Al revés, un retiro fallido con el borrado ya guardado dejaría el archivo público para siempre.
            await filePublication.RemovePublicCopiesAsync(resource, cancellationToken);
            resource.SoftDelete(now);
            auditPublisher.PublishSystem(tenantId, "storage.file.deleted", "file", resource.Id.ToString(), reason, now);
            await files.CommitAsync(cancellationToken);
            LogReleased(logger, ownerType, id.Value, tenantId, reason);
            return true;
        }
        catch
        {
            await AbandonQuietlyAsync(id);
            throw;
        }
    }

    // Un rollback que falla (la conexión se cayó) no tapa el error original: la transacción muere con la conexión y el candado se suelta solo.
    private async Task AbandonQuietlyAsync(FileResourceId id)
    {
        try
        {
            await files.AbandonAsync();
        }
        catch (Exception exception)
        {
            LogAbandonFailed(logger, exception, id.Value);
        }
    }
}
