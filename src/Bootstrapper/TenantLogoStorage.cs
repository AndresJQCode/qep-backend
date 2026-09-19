using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Publica y despublica el logo de un tenant en el bucket público de Storage (decisión 4 del spec
/// 2026-09-19), con `FilePublication` (Storage.Application) para la copia+rollback y sin pasar
/// por `PublishFileHandler`/`SoftDeleteFileHandler` ni por sus permisos — publicar el logo lo
/// autoriza `tenancy.settings.update`, no `storage.file.publish`. Mismo criterio que
/// <see cref="PublicPaymentProofPublisher"/>: un adaptador de Bootstrapper que lanza excepciones
/// de dominio de <b>otro</b> módulo (<see cref="TenantDomainException"/>), porque acá es donde
/// vive la regla de negocio "qué archivo puede ser el logo de un tenant".
/// </summary>
internal sealed class TenantLogoStorage(
    IFileResourceRepository repository,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IStorageUnitOfWork storageUnitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<TenantLogoStorage> logger) : ITenantLogoStorage
{
    // Decisión 6 del spec: sólo estos tres tipos, y hasta 2 MiB. Además de FileUploadPolicy (25
    // MiB): ese tope es el general de subida, éste es el del logo en particular.
    private const long MaxSizeBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> AllowedMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };

    private static readonly Action<ILogger, Guid, Guid, Exception> LogUnpublishFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(6000, nameof(LogUnpublishFailed)),
            "No se pudo retirar el logo anterior (tenant {TenantId}, archivo {FileId}); queda publicado hasta que alguien lo borre.");

    public async Task<TenantLogoPublication> PublishAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, cancellationToken);

        if (resource.Status is not FileResourceStatus.Available)
        {
            throw new TenantDomainException(
                "tenancy.logo.not_available", "The logo file has not finished uploading yet.");
        }

        if (!AllowedMimeTypes.Contains(resource.MimeType))
        {
            throw new TenantDomainException(
                "tenancy.logo.not_image", "The logo must be a PNG, JPEG or WEBP image.");
        }

        if (resource.SizeBytes > MaxSizeBytes)
        {
            throw new TenantDomainException(
                "tenancy.logo.too_large", $"The logo cannot exceed {MaxSizeBytes} bytes.");
        }

        // FilePublication ya valida IsConfigured (storage.public.not_configured) y EnsureDownloadable.
        var publicKey = await filePublication.PublishAsync(resource, cancellationToken);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", clock.UtcNow);
        // Ese commit es de Storage y ocurre antes del de Tenancy (paso 5 del handler, Task 7).
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);

        return new TenantLogoPublication(publicKey);
    }

    public async Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || resource.TenantId != tenantId ||
            resource.Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            // Idempotente (decisión 8 del spec): el commit de Tenancy que dispara esto puede
            // fallar después de que el archivo ya se retiró en un intento anterior.
            return;
        }

        await filePublication.UnpublishAsync(resource, cancellationToken);
        resource.SoftDelete(clock.UtcNow);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.deleted",
            resource.Id.ToString(), "success", clock.UtcNow);
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await UnpublishAsync(tenantId, fileId, cancellationToken);
        }
        catch (Exception exception)
        {
            LogUnpublishFailed(logger, tenantId, fileId, exception);
        }
    }

    public string? GetUrl(string publicKey) =>
        publicStorage.IsConfigured ? publicStorage.GetUrl(publicKey) : null;

    private async Task<FileResource> LoadOwnedAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        // Un solo código para "no existe" y "es de otro tenant" (PublicPaymentProofPublisher.cs:52-60):
        // distinguirlos confirmaría que el id existe en otro tenant.
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new TenantDomainException(
                "tenancy.logo.file_not_found", $"File '{fileId}' was not found in this tenant.");
        }

        if (resource.OwnerType is not FileOwnerType.Tenant || resource.OwnerId != tenantId)
        {
            throw new TenantDomainException(
                "tenancy.logo.not_owned", "The file does not belong to this tenant's logo slot.");
        }

        return resource;
    }
}
