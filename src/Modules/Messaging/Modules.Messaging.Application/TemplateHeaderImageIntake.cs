using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>La revisión y la liberación de la imagen fija del encabezado de una plantilla, compartidas por crear (spec 2026-11-17) y por
/// cambiar la imagen (spec 2026-11-20). Salieron de <see cref="CreateTemplateHandler"/> sin cambiar su comportamiento ni sus logs: el
/// <paramref name="logger"/> es el del handler que la usa.</summary>
internal sealed partial class TemplateHeaderImageIntake(
    IMessagingTemplateImageStore images,
    IMessageTemplateRepository repository,
    IMessagingUnitOfWork unitOfWork,
    ITemplateHeaderImageReleases imageReleases,
    IClock clock,
    ILogger logger)
{
    public static readonly IReadOnlyDictionary<string, string[]> HeaderImageField =
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["headerImageFileId"] = [TemplateMessages.HeaderImage] };

    /// <summary>Completa la subida y revisa el archivo con lo que Storage guardó, no con lo que el navegador declaró. Cada problema sale con
    /// su código y la clave <c>headerImageFileId</c>, que el formulario marca en el campo del encabezado. Devuelve el tipo del archivo.</summary>
    public async Task<string> EnsureUsableImageAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await images.CompleteAsync(tenantId, fileId, cancellationToken);
        var (code, message) = file.State switch
        {
            TemplateImageState.NotFound => (MessagingErrorCodes.TemplateHeaderImageNotFound, "The header image was not found in this tenant."),
            TemplateImageState.NotUploaded => (MessagingErrorCodes.TemplateHeaderImageNotUploaded, "The header image was not uploaded yet."),
            TemplateImageState.Rejected => (MessagingErrorCodes.TemplateHeaderImageRejected, "The header image was rejected by the file scan."),
            _ when !TemplateHeaderImagePolicy.IsAllowedMimeType(file.MimeType) =>
                (MessagingErrorCodes.TemplateHeaderImageUnsupported, "A template header image must be a JPEG or PNG file."),
            _ when file.SizeBytes > TemplateHeaderImagePolicy.MaxSizeBytes =>
                (MessagingErrorCodes.TemplateHeaderImageTooLarge, "A template header image cannot exceed 5 MB."),
            // Antes de publicar: un archivo que el barrido diario de Storage ya podría estar soltando no se toma (ver MaxUploadAge).
            _ when clock.UtcNow - file.CreatedAt > TemplateHeaderImagePolicy.MaxUploadAge =>
                (MessagingErrorCodes.TemplateHeaderImageRejected, "The header image was uploaded too long ago; upload it again."),
            _ => (null, null),
        };
        if (code is not null)
        {
            throw new MessagingDomainException(code, message!, fieldErrors: HeaderImageField);
        }

        return file.MimeType!.Trim().ToLowerInvariant();
    }

    /// <summary>Libera la imagen ya publicada de una creación que falló, con <see cref="CancellationToken.None"/>: la liberación no puede
    /// depender del request que se está cayendo. Si Storage no la libera (R2 caído), deja el aviso
    /// <see cref="MessagingIntegrationEvents.TemplateHeaderImageReleased"/> en el outbox y Storage la libera con reintentos (plan 2026-11-17
    /// Task 8). El aviso se guarda solo: antes se descarta lo que el tracker tenía (la fila y la auditoría que no se pudieron guardar, si el
    /// fallo fue al guardar), para que no viaje con él. Si eso también falla, se registra y sale el error original.
    /// <para>Revisión del bloque C: antes de liberar (por cualquiera de los dos caminos) se pregunta a la base si una plantilla guardada usa
    /// el archivo. Si una lo usa (otra creación con el mismo fileId ganó entre la revisión de arriba y este fallo), no se libera: es su
    /// imagen. Si la pregunta misma falla, tampoco: una imagen pública de más es mejor que quitarle la suya a una plantilla viva.</para></summary>
    public async Task ReleaseOrDeferAsync(Guid tenantId, Guid fileId)
    {
        try
        {
            if (await repository.IsHeaderImageInUseAsync(tenantId, fileId, CancellationToken.None))
            {
                LogReleaseSkippedInUse(logger, tenantId, fileId);
                return;
            }
        }
        catch (Exception exception)
        {
            LogReleaseFailed(logger, exception, tenantId, fileId);
            return;
        }

        try
        {
            await images.ReleaseAsync(tenantId, fileId, CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            LogReleaseDeferred(logger, exception, tenantId, fileId);
        }

        try
        {
            unitOfWork.DiscardPendingChanges();
            imageReleases.Publish(tenantId, fileId, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogReleaseFailed(logger, exception, tenantId, fileId);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The header image {FileId} of a template that failed to be created or changed in tenant {TenantId} could not be released now; the release goes through the outbox.")]
    private static partial void LogReleaseDeferred(ILogger logger, Exception exception, Guid tenantId, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The header image {FileId} of a template that failed to be created or changed in tenant {TenantId} is not released: a saved template uses it.")]
    private static partial void LogReleaseSkippedInUse(ILogger logger, Guid tenantId, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The header image {FileId} of a template that failed to be created or changed in tenant {TenantId} could not be released nor deferred to the outbox; it stays published.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, Guid tenantId, Guid fileId);
}
