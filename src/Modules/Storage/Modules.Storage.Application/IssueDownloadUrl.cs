using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

// Es un comando (no una consulta) porque emitir una URL de descarga reevalúa la autorización
// y registra una entrada de auditoría — tiene efecto commiteado en una unidad de trabajo.
public sealed record IssueDownloadUrlCommand(
    Guid TenantId,
    Guid FileResourceId,
    string? Variant = null)
    : ICommand<DownloadUrlDto>;

public sealed class IssueDownloadUrlHandler(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<IssueDownloadUrlCommand, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> HandleAsync(
        IssueDownloadUrlCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileRead);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Sólo un recurso disponible se puede descargar (invariante de la capacidad).
        resource.EnsureDownloadable();

        var storageKey = string.IsNullOrWhiteSpace(command.Variant)
            ? resource.StorageKey
            : resource.GetVariant(command.Variant).StorageKey;
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            storageKey, cancellationToken);

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.downloaded",
            resource.Id.ToString(),
            string.IsNullOrWhiteSpace(command.Variant)
                ? "success"
                : $"success:{command.Variant}",
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DownloadUrlDto(url.ToString());
    }
}
