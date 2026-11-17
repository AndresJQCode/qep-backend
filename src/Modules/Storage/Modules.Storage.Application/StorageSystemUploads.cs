using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

/// <summary>
/// La subida sin miembro (plan F31). Mismo pipeline que la consola: completar pasa por <see cref="UploadCompletion"/>, con la
/// auditoría a nombre del sistema. Antes, el mismo orden que <see cref="CompleteUploadHandler"/> sin la autorización del miembro: el
/// 404 de otro tenant (y acá también de otro dueño) primero, después el guard de módulo.
/// </summary>
public sealed class StorageSystemUploads(
    IFileResourceRepository repository,
    ITenantModules tenantModules,
    IObjectStorage objectStorage,
    IFileContentInspector contentInspector,
    IImageVariantGenerator imageVariantGenerator,
    IPaymentProofImageProcessor paymentProofImageProcessor,
    IFileScanner scanner,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IClock clock) : IStorageSystemUploads
{
    /// <summary>Plan F37: la descarga de un adjunto vive 5 minutos, igual que el default de la consola, pero fijo.</summary>
    private static readonly TimeSpan DownloadExpiry = TimeSpan.FromMinutes(5);

    private readonly UploadCompletion _completion = new(
        objectStorage,
        contentInspector,
        imageVariantGenerator,
        paymentProofImageProcessor,
        scanner,
        unitOfWork,
        auditPublisher,
        clock);

    public async Task<SystemUploadSession> OpenAsync(
        Guid tenantId,
        FileOwnerType ownerType,
        Guid ownerId,
        string name,
        string mimeType,
        long sizeBytes,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        FileUploadPolicy.ValidateDeclaration(name, mimeType, sizeBytes, maxSizeBytes);

        var id = FileResourceId.New();
        var key = StorageKey.StagingFor(tenantId, id);
        var trimmedMimeType = mimeType.Trim();
        repository.Add(FileResource.CreatePendingUpload(
            id, tenantId, ownerId, ownerType, name.Trim(), trimmedMimeType, sizeBytes, key, clock.UtcNow));

        var uploadUrl = await objectStorage.CreatePresignedUploadUrlAsync(key, trimmedMimeType, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Los dos van firmados en el PUT (R2ObjectStorage): sin el mismo Content-Type la firma no casa, y If-None-Match impide
        // pisar un objeto ya subido.
        return new SystemUploadSession(
            id.Value,
            uploadUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Type"] = trimmedMimeType,
                ["If-None-Match"] = "*",
            });
    }

    public async Task<SystemUploadResult> CompleteAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, ownerType, ownerId, cancellationToken);
        if (resource is null)
        {
            return new SystemUploadResult(SystemUploadOutcome.NotFound);
        }

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken);

        // Completar dos veces no es un error para quien sube desde un navegador (un reintento tras un corte): se responde lo que ya
        // quedó. Borrado o purgado ya no existe para él.
        switch (resource.Status)
        {
            case FileResourceStatus.Available:
                return new SystemUploadResult(SystemUploadOutcome.Available);
            case FileResourceStatus.Quarantined:
                return new SystemUploadResult(SystemUploadOutcome.Rejected);
            case FileResourceStatus.PendingUpload:
                break;
            default:
                return new SystemUploadResult(SystemUploadOutcome.NotFound);
        }

        try
        {
            await _completion.CompleteAsync(resource, actorId: null, cancellationToken);
        }
        catch (PreconditionRequiredException)
        {
            // El PUT todavía no llegó: el recurso sigue PendingUpload y se puede volver a completar.
            return new SystemUploadResult(SystemUploadOutcome.ObjectMissing);
        }
        catch (StorageDomainException)
        {
            // Tamaño distinto del declarado, contenido que no es lo que dice o imagen que no se puede procesar: el núcleo ya lo dejó
            // en cuarentena y lo auditó (o, vacío, no lo pasó de PendingUpload). Para quien sube es lo mismo: rechazado.
            return new SystemUploadResult(SystemUploadOutcome.Rejected);
        }

        return new SystemUploadResult(resource.Status is FileResourceStatus.Available
            ? SystemUploadOutcome.Available
            : SystemUploadOutcome.Rejected);
    }

    public async Task<SystemFileInfo?> FindAvailableAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, ownerType, ownerId, cancellationToken);
        return resource is { Status: FileResourceStatus.Available }
            ? new SystemFileInfo(resource.Id.Value, resource.Name, resource.MimeType, resource.SizeBytes)
            : null;
    }

    /// <summary>Sólo firma archivos de un dueño del sistema (<see cref="SystemOwnedFiles"/>): este puerto no tiene miembro, así que un
    /// archivo de la consola del mismo tenant es el mismo 404 que uno que no existe (revisión final, triage 13).</summary>
    public async Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null
            || resource.TenantId != tenantId
            || !SystemOwnedFiles.Includes(resource.OwnerType)
            || resource.Status is not FileResourceStatus.Available)
        {
            throw new ResourceNotFoundException("storage.file.not_found", "The file resource was not found.");
        }

        return await objectStorage.CreatePresignedDownloadUrlAsync(
            resource.StorageKey, DownloadExpiry, resource.Name, cancellationToken);
    }

    public async Task<SystemStoredFile> StoreAsync(
        Guid tenantId,
        FileOwnerType ownerType,
        Guid ownerId,
        string name,
        string mimeType,
        byte[] content,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (SystemOwnedFiles.Includes(ownerType))
        {
            throw new ArgumentException(
                $"Owner type '{ownerType}' is system-owned; use OpenAsync with its secret owner key.", nameof(ownerType));
        }

        FileUploadPolicy.ValidateDeclaration(name, mimeType, content.LongLength, maxSizeBytes);

        var id = FileResourceId.New();
        var key = StorageKey.StagingFor(tenantId, id);
        var trimmedMimeType = mimeType.Trim();
        var resource = FileResource.CreatePendingUpload(
            id, tenantId, ownerId, ownerType, name.Trim(), trimmedMimeType, content.LongLength, key, clock.UtcNow);
        // Antes de crear nada, como completar en la consola: un dueño de un módulo apagado no sube.
        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken);

        // Primero la fila y después el objeto, como la consola: si la subida falla queda un PendingUpload que el barrido de staging
        // limpia, nunca un objeto sin fila.
        repository.Add(resource);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await objectStorage.UploadAsync(key, content, trimmedMimeType, cancellationToken);

        try
        {
            await _completion.CompleteAsync(resource, actorId: null, cancellationToken);
        }
        catch (StorageDomainException)
        {
            // Tamaño distinto, contenido que no es lo que dice o imagen que no se procesa: ya quedó en cuarentena (ver CompleteAsync).
            return new SystemStoredFile(id.Value, SystemUploadOutcome.Rejected);
        }

        return new SystemStoredFile(id.Value, resource.Status is FileResourceStatus.Available
            ? SystemUploadOutcome.Available
            : SystemUploadOutcome.Rejected);
    }

    // Otro tenant, otro tipo de dueño u otro dueño: lo mismo que un id que no existe (plan F32).
    private async Task<FileResource?> LoadOwnedAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        return resource is not null
            && resource.TenantId == tenantId
            && resource.OwnerType == ownerType
            && resource.OwnerId == ownerId
            ? resource
            : null;
    }
}
