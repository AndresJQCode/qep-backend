using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record CancelUploadCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<CancelUploadResult>;

public sealed record CancelUploadResult(bool Cancelled);

public sealed class CancelUploadHandler(
    IFileResourceRepository repository,
    ITenantModules tenantModules,
    IObjectStorage objectStorage,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CancelUploadCommand, CancelUploadResult>
{
    public async Task<CancelUploadResult> HandleAsync(
        CancelUploadCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileUpload);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Spec 2026-10-07: después del 404 —que no confirma que el id existe en otro tenant— y antes
        // de cualquier otra regla o efecto.
        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken);

        var now = clock.UtcNow;
        await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
        resource.PurgeAbandonedUpload(now);
        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.upload_cancelled",
            resource.Id.ToString(),
            "client_upload_failed",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CancelUploadResult(true);
    }
}
