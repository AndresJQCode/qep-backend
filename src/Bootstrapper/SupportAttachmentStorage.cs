using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Bootstrapper;

/// <summary>
/// Los adjuntos del formulario público de Soporte sobre la subida sin miembro de Storage (plan F30 a F32). Support no ve a Storage
/// (<c>SupportLayerTests</c>), así que el dueño <see cref="FileOwnerType.SupportTicket"/> y el tope de 10 MB se ponen acá. El
/// <c>uploadKey</c> es el <c>OwnerId</c> del archivo: Storage exige el par en cada operación y uno que no casa responde igual que un id
/// que no existe. Nada de esto pasa por <c>StorageAuthorization</c>: la consola sigue igual.
/// </summary>
internal sealed class SupportAttachmentStorage(IStorageSystemUploads uploads) : ISupportAttachmentStorage
{
    public async Task<SupportAttachmentUpload> OpenAsync(
        Guid tenantId, Guid uploadKey, string name, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        try
        {
            var session = await uploads.OpenAsync(
                tenantId,
                FileOwnerType.SupportTicket,
                uploadKey,
                name,
                mimeType,
                sizeBytes,
                SupportPublicAttachmentRules.MaxSizeBytes,
                cancellationToken);
            return new SupportAttachmentUpload(session.FileId, session.UploadUrl, session.Headers);
        }
        catch (StorageDomainException exception)
        {
            // El validador de Soporte ya filtró nombre, tipo y tamaño con las mismas reglas (SupportAttachmentParityTests): llegar acá
            // es una declaración que Storage ve distinto. La página recibe el código de Soporte, no el de Storage.
            throw new SupportDomainException(SupportErrorCodes.PublicAttachmentInvalid, $"The attachment was rejected ({exception.Code}).");
        }
    }

    public async Task<SupportAttachmentCompletion> CompleteAsync(
        Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken) =>
        (await uploads.CompleteAsync(tenantId, fileId, FileOwnerType.SupportTicket, uploadKey, cancellationToken)).Outcome switch
        {
            SystemUploadOutcome.Available => SupportAttachmentCompletion.Available,
            SystemUploadOutcome.Rejected => SupportAttachmentCompletion.Rejected,
            SystemUploadOutcome.ObjectMissing => SupportAttachmentCompletion.NotUploaded,
            _ => SupportAttachmentCompletion.NotFound,
        };

    public async Task<SupportAttachmentFile?> FindAvailableAsync(
        Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken) =>
        await uploads.FindAvailableAsync(tenantId, fileId, FileOwnerType.SupportTicket, uploadKey, cancellationToken) is { } file
            ? new SupportAttachmentFile(file.FileId, file.Name, file.MimeType, file.SizeBytes)
            : null;

    public Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        uploads.CreateDownloadUrlAsync(tenantId, fileId, cancellationToken);
}
