using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.TemplateImages;

internal interface ITemplateHeaderImageSweep
{
    /// <returns>Cuántos archivos soltó.</returns>
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

// Spec 2026-11-17, «Closing the residual gaps»: la red final para los caminos que hoy sólo registran (la revisión de uso que falla, el
// aviso del outbox que no se escribe, una subida cuya creación nunca llegó). Toma las imágenes de plantilla vivas con más de 24 h, le
// pregunta a Messaging por tenant cuáles usa una fila de plantilla y suelta el resto: copias públicas fuera (aun sin clave guardada),
// borrado lógico y auditoría del sistema, guardado por archivo.
//
// Recorre por cursor (created_at, id) en páginas de BatchSize hasta el final: las imágenes vivas son las más viejas y nunca salen del
// conjunto, así que mirar sólo la primera página dejaría ciego al barrido en cuanto haya más de BatchSize imágenes vivas en total: la
// página es de todos los tenants, no de uno.
//
// Falla cerrado: si Messaging no responde por un tenant, no se suelta nada de ese tenant en esa página. Un archivo que falla (R2, la base)
// se registra, se deshace su transacción y el barrido sigue; mañana vuelve a aparecer.
internal sealed partial class TemplateHeaderImageSweep(
    ITemplateHeaderImageFiles files,
    ITemplateImageReferences references,
    FilePublication filePublication,
    IStorageAuditPublisher auditPublisher,
    ILogger<TemplateHeaderImageSweep> logger) : ITemplateHeaderImageSweep
{
    internal const int BatchSize = 200;
    internal const string Reason = "template_header_image_unreferenced";
    internal static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Template header image sweep could not ask which images tenant {TenantId} still references; its images are kept.")]
    private static partial void LogReferencesFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Template header image sweep could not release file {FileId}; the next sweep retries it.")]
    private static partial void LogFileFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Template header image sweep released file {FileId} of tenant {TenantId}: no template references it.")]
    private static partial void LogReleased(ILogger logger, Guid fileId, Guid tenantId);

    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var createdBefore = now - Grace;
        var released = 0;
        TemplateImageCandidate? after = null;
        while (true)
        {
            var page = await files.ListStaleAsync(createdBefore, after, BatchSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1];
            // La consulta ya filtra por dueño; un archivo de otro dueño nunca se pregunta ni se suelta aunque llegara.
            foreach (var tenant in page.Where(candidate => candidate.OwnerType == FileOwnerType.MessageTemplate).GroupBy(candidate => candidate.TenantId))
            {
                released += await SweepTenantAsync(tenant.Key, tenant.ToList(), now, cancellationToken);
            }

            if (page.Count < BatchSize)
            {
                break;
            }
        }

        return released;
    }

    private async Task<int> SweepTenantAsync(
        Guid tenantId, IReadOnlyList<TemplateImageCandidate> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> referenced;
        try
        {
            referenced = await references.FindReferencedAsync(
                tenantId, candidates.Select(candidate => candidate.Id.Value).ToList(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogReferencesFailed(logger, exception, tenantId);
            return 0;
        }

        var released = 0;
        foreach (var candidate in candidates.Where(candidate => !referenced.Contains(candidate.Id.Value)))
        {
            if (await ReleaseAsync(tenantId, candidate.Id, now, cancellationToken))
            {
                released++;
            }
        }

        return released;
    }

    private async Task<bool> ReleaseAsync(Guid tenantId, FileResourceId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            // Bajo candado y releído: otra réplica que lo tenga o ya lo haya soltado lo deja fuera, sin auditar dos veces.
            var resource = await files.ClaimAsync(tenantId, id, cancellationToken);
            if (resource is null)
            {
                return false;
            }

            // Revisión final: entre la pregunta de la página y el reclamo, una creación que reusa un id viejo pudo guardar su fila. Se
            // vuelve a preguntar por este archivo bajo el candado; si ya lo usa una plantilla, se suelta el candado sin tocarlo. Un fallo
            // de la pregunta cae al catch: falla cerrado y no se suelta. La ventana que queda (la creación guarda después de esta
            // pregunta) la cierra Messaging, que no acepta una imagen subida hace más de 12 h.
            var stillReferenced = await references.FindReferencedAsync(tenantId, [id.Value], cancellationToken);
            if (stillReferenced.Contains(id.Value))
            {
                await AbandonQuietlyAsync(id);
                return false;
            }

            // Primero las copias públicas y después el guardado, como el procesador del outbox: borrar una clave que no existe no
            // falla, así que un guardado fallido sólo repite el retiro mañana.
            await filePublication.RemovePublicCopiesAsync(resource, cancellationToken);
            resource.SoftDelete(now);
            auditPublisher.PublishSystem(tenantId, "storage.file.deleted", "file", resource.Id.ToString(), Reason, now);
            await files.CommitAsync(cancellationToken);
            LogReleased(logger, id.Value, tenantId);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbandonQuietlyAsync(id);
            throw;
        }
        catch (Exception exception)
        {
            LogFileFailed(logger, exception, id.Value);
            await AbandonQuietlyAsync(id);
            return false;
        }
    }

    // Un rollback que falla (la conexión se cayó) no corta el barrido: la transacción muere con la conexión y el candado se suelta solo.
    private async Task AbandonQuietlyAsync(FileResourceId id)
    {
        try
        {
            await files.AbandonAsync();
        }
        catch (Exception exception)
        {
            LogFileFailed(logger, exception, id.Value);
        }
    }
}
