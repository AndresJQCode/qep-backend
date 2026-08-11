using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record SoftDeleteFileCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<SoftDeleteResult>;

public sealed class SoftDeleteFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SoftDeleteFileCommand, SoftDeleteResult>
{
    public async Task<SoftDeleteResult> HandleAsync(
        SoftDeleteFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileDelete);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        var now = clock.UtcNow;
        if (resource.PublicStorageKey is { } publicKey)
        {
            await publicStorage.DeleteAsync(publicKey, cancellationToken);
            foreach (var variant in resource.Variants)
            {
                await publicStorage.DeleteAsync(
                    StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
            }
            resource.Unpublish(now);
        }
        // Borrado lógico; el objeto se retiene hasta que pase la ventana de retención.
        resource.SoftDelete(now);

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.deleted",
            resource.Id.ToString(),
            "success",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SoftDeleteResult(true);
    }
}
