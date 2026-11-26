using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-20: el PDF de un contrato o de un registro de RRHH, sobre Storage (calco de <see cref="MessagingTemplateImageStore"/>).
/// El archivo es de dueño de sistema <see cref="FileOwnerType.HrDocument"/> con el tenant como dueño: sube por
/// <see cref="IStorageSystemUploads"/> (staging, magic bytes, antivirus) y no aparece en la consola de Storage. Lo autoriza
/// <c>hr.contract.manage</c>, no los permisos de Storage. Hr no referencia Storage, así que el dueño y el tope de 10 MB se ponen acá.
/// </summary>
internal sealed class HrDocumentStore(IStorageSystemUploads uploads) : IHrDocumentStore
{
    private const FileOwnerType OwnerType = FileOwnerType.HrDocument;

    public async Task<HrUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        // La tabla general de Storage también admite imágenes y documentos de Office: que sólo entre un PDF es regla de Hr.
        if (!HrDocumentPolicy.IsValidDeclaration(fileName, mimeType, sizeBytes))
        {
            throw new HrDomainException(HrErrorCodes.FileInvalid, "The file must be a PDF of up to 10 MB.");
        }

        SystemUploadSession session;
        try
        {
            session = await uploads.OpenAsync(
                tenantId, OwnerType, tenantId, fileName, mimeType, sizeBytes, HrDocumentPolicy.MaxSizeBytes, cancellationToken);
        }
        // El caso de uso ya filtró nombre, tipo y tamaño: llegar acá es una declaración que Storage ve distinto. La pantalla recibe el
        // código de Hr, no uno de Storage que no conoce.
        catch (StorageDomainException exception) when (exception.Code is
            "storage.file.type_not_allowed" or "storage.file.mime_mismatch" or "storage.file.size_invalid")
        {
            throw new HrDomainException(HrErrorCodes.FileInvalid, "The file must be a PDF of up to 10 MB.");
        }

        return new HrUploadSession(session.FileId, session.UploadUrl, session.Headers);
    }

    public async Task<HrDocumentFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var result = await uploads.CompleteAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken);
        switch (result.Outcome)
        {
            case SystemUploadOutcome.Available:
                // Lo guardado, no lo declarado: el nombre que vale es el que Storage verificó.
                return await uploads.FindAvailableAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken) is { } info
                    ? new HrDocumentFile(HrDocumentState.Available, info.Name)
                    : new HrDocumentFile(HrDocumentState.NotFound, null);
            case SystemUploadOutcome.ObjectMissing:
                return new HrDocumentFile(HrDocumentState.NotUploaded, null);
            case SystemUploadOutcome.Rejected:
                return new HrDocumentFile(HrDocumentState.Rejected, null);
            default:
                return new HrDocumentFile(HrDocumentState.NotFound, null);
        }
    }

    // Se comprueba primero que es un HrDocument disponible de este tenant: la URL firmada de Storage sólo exige un dueño de
    // sistema, y un adjunto de Soporte no debe poder firmarse desde Hr.
    public async Task<Uri?> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        await uploads.FindAvailableAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken) is null
            ? null
            : await uploads.CreateDownloadUrlAsync(tenantId, fileId, cancellationToken);
}
