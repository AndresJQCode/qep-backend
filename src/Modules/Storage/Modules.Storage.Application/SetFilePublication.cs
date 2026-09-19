using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record PublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed record UnpublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed class PublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    FilePublication filePublication,
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
        // Fix round 1 (Important): precedencia original, de antes de extraer FilePublication —
        // el bucket sin configurar se detecta antes de cargar el archivo, no después.
        // FilePublication repite este chequeo porque el adaptador de Tenancy la llama directo.
        if (!publicStorage.IsConfigured)
        {
            throw new StorageDomainException(
                "storage.public.not_configured",
                "Public image storage is not configured.");
        }

        var resource = await LoadAsync(repository, command.TenantId, command.FileId, cancellationToken);
        // Spec 2026-09-16, D15: un comprobante sólo llega al público por el movimiento. Por acá
        // copiaría desde su temporal, que después de moverse ya no existe.
        PaymentProofGuard.EnsureNotPaymentProof(resource);

        var publicKey = await filePublication.PublishAsync(resource, cancellationToken);

        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", clock.UtcNow);
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
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
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

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        if (resource.PublicStorageKey is not null)
        {
            await filePublication.UnpublishAsync(resource, cancellationToken);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "storage.file.unpublished",
                resource.Id.ToString(), "success", clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return resource.ToDto(publicStorage);
    }
}
