using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record UpdateFileMetadataCommand(
    Guid TenantId,
    Guid FileResourceId,
    string? Category,
    IReadOnlyList<string> Tags) : ICommand<FileResourceDto>;

public sealed class UpdateFileMetadataHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<UpdateFileMetadataCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        UpdateFileMetadataCommand command,
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

        var now = clock.UtcNow;
        resource.UpdateMetadata(command.Category, command.Tags, now);
        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.metadata_updated",
            resource.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return resource.ToDto();
    }
}
