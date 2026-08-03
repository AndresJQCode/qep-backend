using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record CreateUploadSessionCommand(
    Guid TenantId,
    Guid OwnerId,
    FileOwnerType OwnerType,
    string Name,
    string MimeType,
    long SizeBytes) : ICommand<UploadSessionDto>;

public sealed class CreateUploadSessionHandler(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IStorageUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CreateUploadSessionCommand, UploadSessionDto>
{
    public async Task<UploadSessionDto> HandleAsync(
        CreateUploadSessionCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileUpload);

        FileUploadPolicy.ValidateDeclaration(command.Name, command.MimeType, command.SizeBytes);

        var now = clock.UtcNow;
        var id = FileResourceId.New();
        var key = StorageKey.StagingFor(command.TenantId, id);
        var resource = FileResource.CreatePendingUpload(
            id,
            command.TenantId,
            command.OwnerId,
            command.OwnerType,
            command.Name,
            command.MimeType,
            command.SizeBytes,
            key,
            now);
        repository.Add(resource);

        var uploadUrl = await objectStorage.CreatePresignedUploadUrlAsync(
            key, command.MimeType, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new UploadSessionDto(id.Value, uploadUrl.ToString(), key);
    }
}
