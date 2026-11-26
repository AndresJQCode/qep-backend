using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17: la imagen fija del encabezado de una plantilla de WhatsApp, sobre Storage. El archivo es de dueño de sistema
/// <see cref="FileOwnerType.MessageTemplate"/> con el tenant como dueño: sube por <see cref="IStorageSystemUploads"/> (staging, magic
/// bytes, antivirus) y no aparece en la consola de Storage. Publicar y liberar usan <see cref="FilePublication"/> sin pasar por
/// <c>PublishFileHandler</c>/<c>SoftDeleteFileHandler</c> ni por sus permisos: los autoriza <c>messaging.template.manage</c>, como el logo
/// del tenant (<see cref="TenantLogoStorage"/>). Por eso este adaptador lanza el error de dominio de Messaging.
/// </summary>
internal sealed class MessagingTemplateImageStore(
    IStorageSystemUploads uploads,
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IStorageUnitOfWork storageUnitOfWork,
    IExecutionContext executionContext,
    IClock clock) : IMessagingTemplateImageStore
{
    private const FileOwnerType OwnerType = FileOwnerType.MessageTemplate;

    public async Task<TemplateImageUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        SystemUploadSession session;
        try
        {
            session = await uploads.OpenAsync(
                tenantId, OwnerType, tenantId, fileName, mimeType, sizeBytes, TemplateHeaderImagePolicy.MaxSizeBytes, cancellationToken);
        }
        catch (StorageDomainException exception) when (TranslateDeclarationRejection(exception) is { } translated)
        {
            throw translated;
        }

        return new TemplateImageUploadSession(session.FileId, session.UploadUrl, session.Headers);
    }

    // La política de Mensajería sólo mira el tipo declarado; la extensión y el tope los revisa FileUploadPolicy. Su rechazo sale con el
    // error de la imagen (sin mapa errors, como en la sesión), porque el formulario de plantillas no conoce los códigos de Storage. Un
    // nombre inválido no se traduce: sigue siendo storage.file.name_*.
    private static MessagingDomainException? TranslateDeclarationRejection(StorageDomainException exception) =>
        exception.Code switch
        {
            "storage.file.type_not_allowed" or "storage.file.mime_mismatch" => new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageUnsupported, "A template header image must be a JPEG or PNG file.", exception),
            "storage.file.size_invalid" => new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageTooLarge, "A template header image cannot exceed 5 MB.", exception),
            _ => null,
        };

    public async Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var result = await uploads.CompleteAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken);
        switch (result.Outcome)
        {
            case SystemUploadOutcome.Available:
                // Lo guardado, no lo declarado: el tipo y el tamaño que valen son los que Storage verificó. La creación del archivo viaja
                // para que Messaging no tome uno que el barrido diario ya podría soltar (TemplateHeaderImagePolicy.MaxUploadAge).
                return await uploads.FindAvailableAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken) is { } info
                    ? new TemplateImageFile(TemplateImageState.Available, info.MimeType, info.SizeBytes, info.CreatedAt)
                    : new TemplateImageFile(TemplateImageState.NotFound, null, 0, default);
            case SystemUploadOutcome.ObjectMissing:
                return new TemplateImageFile(TemplateImageState.NotUploaded, null, 0, default);
            case SystemUploadOutcome.Rejected:
                return new TemplateImageFile(TemplateImageState.Rejected, null, 0, default);
            default:
                return new TemplateImageFile(TemplateImageState.NotFound, null, 0, default);
        }
    }

    public async Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, cancellationToken);
        resource.EnsureDownloadable();
        return await objectStorage.DownloadAsync(resource.StorageKey, cancellationToken);
    }

    public async Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, cancellationToken);
        // FilePublication valida que el bucket público esté configurado y que el archivo sea una imagen Available.
        var publicKey = await filePublication.PublishAsync(resource, cancellationToken);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.published", resource.Id.ToString(), "success", clock.UtcNow);
        // Este commit es de Storage y va antes del de Messaging: si el de Messaging falla, quien llama libera la imagen.
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);
        return publicStorage.GetUrl(publicKey);
    }

    public async Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || !IsTemplateImageOf(resource, tenantId)
            || resource.Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            // Idempotente: la liberación se reintenta (compensación de crear, outbox de borrar) y puede encontrar el archivo ya retirado.
            // Uno ajeno no se toca ni se confirma que existe.
            return;
        }

        await filePublication.RemovePublicCopiesAsync(resource, cancellationToken);
        resource.SoftDelete(clock.UtcNow);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.deleted", resource.Id.ToString(), "success", clock.UtcNow);
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static bool IsTemplateImageOf(FileResource resource, Guid tenantId) =>
        resource.TenantId == tenantId && resource.OwnerType == OwnerType && resource.OwnerId == tenantId;

    // No existe, es de otro tenant o no es una imagen de plantilla: un solo código, para no confirmar que el id existe en otro lado.
    private async Task<FileResource> LoadOwnedAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        return resource is not null && IsTemplateImageOf(resource, tenantId)
            ? resource
            : throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageNotFound,
                $"Template header image '{fileId}' was not found in this tenant.",
                fieldErrors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["headerImageFileId"] = ["No encontramos la imagen. Vuelve a subirla."],
                });
    }
}
