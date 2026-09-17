using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record CompleteUploadCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<FileResourceDto>;

public sealed class CompleteUploadHandler(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IFileContentInspector contentInspector,
    IImageVariantGenerator imageVariantGenerator,
    IPaymentProofImageProcessor paymentProofImageProcessor,
    IFileScanner scanner,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CompleteUploadCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        CompleteUploadCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileUpload);

        var resource = await LoadAsync(command.TenantId, command.FileResourceId, cancellationToken);

        var stored = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
            ?? throw new PreconditionRequiredException(
                "storage.object.missing",
                "The object has not been uploaded to storage yet.");

        var now = clock.UtcNow;
        var declaredSizeBytes = resource.SizeBytes;
        resource.CompleteUpload(stored.Checksum, stored.SizeBytes, now);

        if (stored.SizeBytes != declaredSizeBytes || stored.SizeBytes > FileUploadPolicy.MaxSizeBytes)
        {
            resource.Quarantine(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.size_invalid",
                "The uploaded object exceeds the maximum allowed size.");
        }

        var content = await objectStorage.DownloadAsync(resource.StorageKey, cancellationToken);
        if (!contentInspector.Matches(resource.Name, resource.MimeType, content))
        {
            resource.Quarantine(now);
            auditPublisher.Publish(
                resource.TenantId,
                executionContext.SubjectId,
                "storage.file.rejected",
                resource.Id.ToString(),
                "content_type_mismatch",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.content_mismatch",
                "The file content does not match its declared type.");
        }

        string? promotedStagingKey = null;
        var verdict = await scanner.ScanAsync(content, cancellationToken);
        if (verdict is FileScanResult.Clean && resource.OwnerType is FileOwnerType.PaymentProof)
        {
            await KeepPaymentProofInStagingAsync(resource, content, now, cancellationToken);
        }
        else if (verdict is FileScanResult.Clean)
        {
            var stagingKey = resource.StorageKey;
            var finalKey = StorageKey.FinalFor(resource.TenantId, resource.Id, resource.CreatedAt);
            IReadOnlyList<GeneratedFileVariant> variants;
            try
            {
                variants = imageVariantGenerator.Supports(resource.MimeType)
                    ? await imageVariantGenerator.GenerateAsync(content, cancellationToken)
                    : [];
            }
            catch (StorageDomainException)
            {
                await QuarantineUnprocessableImageAsync(resource, now, cancellationToken);
                throw;
            }
            await objectStorage.PromoteAsync(
                stagingKey, finalKey, stored.Checksum, cancellationToken);
            foreach (var variant in variants)
            {
                var variantKey = StorageKey.VariantFor(
                    finalKey, variant.Name, variant.Extension);
                await objectStorage.UploadAsync(
                    variantKey,
                    variant.Content,
                    variant.MimeType,
                    cancellationToken);
                resource.AddVariant(
                    variant.Name,
                    variantKey,
                    variant.MimeType,
                    variant.Width,
                    variant.Height,
                    variant.Content.LongLength);
            }
            resource.Promote(finalKey, now);
            promotedStagingKey = stagingKey;
        }
        else
        {
            resource.Quarantine(now);
        }

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.uploaded",
            resource.Id.ToString(),
            verdict is FileScanResult.Clean ? "success" : "quarantined",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (promotedStagingKey is not null)
        {
            await objectStorage.DeleteAsync(promotedStagingKey, cancellationToken);
        }
        return resource.ToDto();
    }

    // Spec 2026-09-16, D4 y D8: un comprobante no se promueve a files/ ni lleva miniatura; espera en
    // staging/ hasta que se adjunta a un pedido. Si es imagen se reemplaza ahí mismo por su versión
    // procesada (D7), así la conversión sólo tiene que moverlo; un PDF queda tal cual (D1).
    private async Task KeepPaymentProofInStagingAsync(
        FileResource resource,
        byte[] content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (paymentProofImageProcessor.Supports(resource.MimeType))
        {
            ProcessedPaymentProofImage processed;
            try
            {
                processed = await paymentProofImageProcessor.ProcessAsync(content, cancellationToken);
            }
            catch (StorageDomainException)
            {
                await QuarantineUnprocessableImageAsync(resource, now, cancellationToken);
                throw;
            }

            await objectStorage.UploadAsync(
                resource.StorageKey, processed.Content, processed.MimeType, cancellationToken);
            // El checksum es el que calcula el almacenamiento sobre lo que quedó guardado, igual que
            // al completar cualquier subida: se vuelve a leer en vez de calcularlo acá.
            var replaced = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
                ?? throw new PreconditionRequiredException(
                    "storage.object.missing",
                    "The object has not been uploaded to storage yet.");
            resource.ReplaceContentWithProcessedImage(
                processed.MimeType, processed.Extension, replaced.Checksum, replaced.SizeBytes, now);
        }

        resource.MarkClean(now);
    }

    private async Task QuarantineUnprocessableImageAsync(
        FileResource resource,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        resource.Quarantine(now);
        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.rejected",
            resource.Id.ToString(),
            "image_processing_failed",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<FileResource> LoadAsync(
        Guid tenantId, Guid fileResourceId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(
            new FileResourceId(fileResourceId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");

        if (resource.TenantId != tenantId)
        {
            // No filtrar la existencia entre tenants.
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        return resource;
    }
}
