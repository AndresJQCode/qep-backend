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

        // Fix round 1 del Bloque D: un dueño de sistema sólo se crea por su camino (IStorageSystemUploads), y uno generado por el
        // servidor (el export de WMS) sólo lo escribe su adaptador. Mismo 422 que un tipo que no existe.
        if (SystemOwnedFiles.Includes(command.OwnerType) || ServerGeneratedFiles.Includes(command.OwnerType))
        {
            throw new StorageDomainException(
                "storage.file.owner_type_invalid",
                "The owner type is not one of the supported values.");
        }

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
        return new UploadSessionDto(id.Value, uploadUrl.AbsoluteUri, key);
    }
}
