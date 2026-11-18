using BuildingBlocks.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// El núcleo de completar una subida, común al handler de la consola (<see cref="CompleteUploadHandler"/>) y a la subida sin miembro
/// (<see cref="StorageSystemUploads"/>, plan F31 de Soporte fase 3). Quien llama ya autorizó, cargó el recurso de su tenant y pasó el
/// guard de módulo; acá van, en este orden, el stat del objeto, el tamaño, los magic bytes, el antivirus, las variantes o el
/// comprobante, la promoción, la auditoría y el guardado. No conoce al llamador: con <c>actorId</c> la auditoría sale a nombre de esa
/// persona y sin él, de sistema. Lanza las mismas excepciones que lanzaba el handler, que es el contrato de la consola.
/// </summary>
internal sealed class UploadCompletion(
    IObjectStorage objectStorage,
    IFileContentInspector contentInspector,
    IImageVariantGenerator imageVariantGenerator,
    IPaymentProofImageProcessor paymentProofImageProcessor,
    IChatbotMediaProcessing chatbotMediaProcessing,
    IFileScanner scanner,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IClock clock)
{
    public async Task CompleteAsync(FileResource resource, Guid? actorId, CancellationToken cancellationToken)
    {
        var stored = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
            ?? throw new PreconditionRequiredException(
                "storage.object.missing",
                "The object has not been uploaded to storage yet.");

        var now = clock.UtcNow;
        var declaredSizeBytes = resource.SizeBytes;
        resource.CompleteUpload(stored.Checksum, stored.SizeBytes, now);

        // El tamaño guardado tiene que ser el declarado: la URL firmada no lo acota, y el declarado ya pasó por el tope de quien abrió
        // la sesión (25 MB en la consola, el suyo en una subida sin miembro). El techo es el del dueño: 100 MB para un ChatbotMedia.
        if (stored.SizeBytes != declaredSizeBytes || stored.SizeBytes > FileUploadPolicy.MaxSizeBytesFor(resource.OwnerType))
        {
            resource.Quarantine(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.size_invalid",
                "The uploaded object exceeds the maximum allowed size.");
        }

        var content = await objectStorage.DownloadAsync(resource.StorageKey, cancellationToken);
        if (!ContentMatches(resource, content))
        {
            resource.Quarantine(now);
            Audit(resource, actorId, "storage.file.rejected", "content_type_mismatch", now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.content_mismatch",
                "The file content does not match its declared type.");
        }

        string? promotedStagingKey = null;
        var verdict = await scanner.ScanAsync(content, cancellationToken);
        if (verdict is FileScanResult.Clean && resource.OwnerType is FileOwnerType.PaymentProof)
        {
            await KeepPaymentProofInStagingAsync(resource, actorId, content, now, cancellationToken);
        }
        else if (verdict is FileScanResult.Clean)
        {
            var promotedChecksum = stored.Checksum;
            if (NeedsShrinking(resource, content))
            {
                (content, promotedChecksum) = await ShrinkChatbotImageAsync(resource, actorId, content, now, cancellationToken);
            }

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
                await QuarantineUnprocessableImageAsync(resource, actorId, now, cancellationToken);
                throw;
            }
            await objectStorage.PromoteAsync(
                stagingKey, finalKey, promotedChecksum, cancellationToken);
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

        Audit(resource, actorId, "storage.file.uploaded", verdict is FileScanResult.Clean ? "success" : "quarantined", now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (promotedStagingKey is not null)
        {
            await objectStorage.DeleteAsync(promotedStagingKey, cancellationToken);
        }
    }

    // Spec 2026-11-18: el inspector de la consola no conoce video ni audio. Un ChatbotMedia lo revisa el sniffer de Mensajería (por el
    // puerto) con su cabecera y la extensión del nombre; cualquier otro dueño, el inspector de siempre.
    private bool ContentMatches(FileResource resource, byte[] content) =>
        resource.OwnerType is FileOwnerType.ChatbotMedia
            ? chatbotMediaProcessing.Matches(resource.Name, content.AsSpan(0, Math.Min(64, content.Length)))
            : contentInspector.Matches(resource.Name, resource.MimeType, content);

    // Spec 2026-11-18: Meta no baja por link una imagen de más de 5 MB. Una imagen de chatbot por encima se comprime antes de promover.
    private static bool NeedsShrinking(FileResource resource, byte[] content) =>
        resource.OwnerType is FileOwnerType.ChatbotMedia
        && resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && content.LongLength > FileUploadPolicy.ChatbotMediaLinkImageMaxBytes;

    // La imagen comprimida reemplaza a la original en la misma clave de staging, como un comprobante (D7): de ahí sale a variantes y a
    // la promoción. Ilegible = cuarentena, igual que una imagen que el generador de variantes no puede leer.
    private async Task<(byte[] Content, string Checksum)> ShrinkChatbotImageAsync(
        FileResource resource,
        Guid? actorId,
        byte[] content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var processed = await chatbotMediaProcessing.ShrinkAsync(content, cancellationToken);
        if (processed is null)
        {
            await QuarantineUnprocessableImageAsync(resource, actorId, now, cancellationToken);
            throw new StorageDomainException(
                "storage.image.invalid",
                "The uploaded image could not be decoded.");
        }

        await objectStorage.UploadAsync(resource.StorageKey, processed.Content, processed.MimeType, cancellationToken);
        // El checksum es el que calcula el almacenamiento sobre lo que quedó guardado (ver KeepPaymentProofInStagingAsync).
        var replaced = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
            ?? throw new PreconditionRequiredException(
                "storage.object.missing",
                "The object has not been uploaded to storage yet.");
        resource.ReplaceContentWithProcessedImage(
            processed.MimeType, processed.Extension, replaced.Checksum, replaced.SizeBytes, now);
        return (processed.Content, replaced.Checksum);
    }

    // Spec 2026-09-16, D4 y D8: un comprobante no se promueve a files/ ni lleva miniatura; espera en
    // staging/ hasta que se adjunta a un pedido. Si es imagen se reemplaza ahí mismo por su versión
    // procesada (D7), así la conversión sólo tiene que moverlo; un PDF queda tal cual (D1).
    private async Task KeepPaymentProofInStagingAsync(
        FileResource resource,
        Guid? actorId,
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
                await QuarantineUnprocessableImageAsync(resource, actorId, now, cancellationToken);
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
        Guid? actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        resource.Quarantine(now);
        Audit(resource, actorId, "storage.file.rejected", "image_processing_failed", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // Con persona, la auditoría de siempre; sin ella, la de sistema (actor vacío, como los barridos de Storage).
    private void Audit(FileResource resource, Guid? actorId, string action, string outcome, DateTimeOffset now)
    {
        if (actorId is { } actor)
        {
            auditPublisher.Publish(resource.TenantId, actor, action, resource.Id.ToString(), outcome, now);
        }
        else
        {
            auditPublisher.PublishSystem(resource.TenantId, action, "file", resource.Id.ToString(), outcome, now);
        }
    }
}
