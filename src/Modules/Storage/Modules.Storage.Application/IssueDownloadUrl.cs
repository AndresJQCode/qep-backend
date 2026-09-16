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
    IPublicObjectStorage publicObjectStorage,
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

        string url;
        if (string.IsNullOrWhiteSpace(command.Variant)
            && resource.OwnerType is FileOwnerType.PaymentProof
            && resource.PublicStorageKey is { } publicKey)
        {
            // Spec 2026-09-16, D5: un comprobante movido vive en el bucket público y su temporal ya no
            // existe. Se abre en una pestaña en vez de bajarse con su nombre, porque un objeto público
            // no lleva Content-Disposition por request: es el cambio aceptado en la sección 3, y es lo
            // mismo que hace el enlace «Ver» del Excel.
            url = publicObjectStorage.GetUrl(publicKey);
        }
        else
        {
            var storageKey = string.IsNullOrWhiteSpace(command.Variant)
                ? resource.StorageKey
                : resource.GetVariant(command.Variant).StorageKey;
            // Con el nombre original del recurso, que firma `Content-Disposition: attachment`. Sin
            // el, R2 sirve el objeto sin disposicion y el navegador **abre** el PDF o la imagen en
            // una pestana en vez de bajarlos: el endpoint se llama "download-url" y hasta ahora no
            // descargaba nada. Una variante conserva el nombre del original -- lo que cambia es el
            // tamano, no que archivo es.
            var signed = await objectStorage.CreatePresignedDownloadUrlAsync(
                storageKey, resource.Name, cancellationToken);
            url = signed.AbsoluteUri;
        }

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
        return new DownloadUrlDto(url);
    }
}
