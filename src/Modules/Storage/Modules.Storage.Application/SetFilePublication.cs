using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record PublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed record UnpublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed class PublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<PublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        PublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);
        if (!publicStorage.IsConfigured)
        {
            throw new StorageDomainException(
                "storage.public.not_configured",
                "Public image storage is not configured.");
        }

        var resource = await LoadAsync(repository, command.TenantId, command.FileId, cancellationToken);
        resource.EnsureDownloadable();
        var publicKey = resource.PublicStorageKey ?? StorageKey.PublicFor(
            resource.TenantId, resource.Id, resource.Name);
        var now = clock.UtcNow;
        // Validate all publication invariants before creating any public object.
        resource.Publish(publicKey, now);
        var copiedKeys = new List<string>();

        try
        {
            await publicStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
            copiedKeys.Add(publicKey);
            foreach (var variant in resource.Variants)
            {
                var variantKey = StorageKey.PublicVariantFor(publicKey, variant);
                await publicStorage.CopyFromPrivateAsync(variant.StorageKey, variantKey, cancellationToken);
                copiedKeys.Add(variantKey);
            }
        }
        catch
        {
            foreach (var key in copiedKeys)
            {
                try { await publicStorage.DeleteAsync(key, CancellationToken.None); }
                catch { /* best-effort rollback; retrying publish is safe */ }
            }
            throw;
        }

        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return resource.ToDto(publicStorage);
    }

    internal static async Task<FileResource> LoadAsync(
        IFileResourceRepository repository, Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new ResourceNotFoundException("storage.file.not_found", "The file resource was not found.");
        }
        return resource;
    }
}

public sealed class UnpublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<UnpublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        UnpublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);
        var resource = await PublishFileHandler.LoadAsync(
            repository, command.TenantId, command.FileId, cancellationToken);

        if (resource.PublicStorageKey is { } publicKey)
        {
            await publicStorage.DeleteAsync(publicKey, cancellationToken);
            foreach (var variant in resource.Variants)
            {
                await publicStorage.DeleteAsync(
                    StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
            }
            resource.Unpublish(clock.UtcNow);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "storage.file.unpublished",
                resource.Id.ToString(), "success", clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return resource.ToDto(publicStorage);
    }
}
