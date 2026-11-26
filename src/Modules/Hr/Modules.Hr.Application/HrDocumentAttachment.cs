using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Todo <c>documentFileId</c> de una escritura completa la subida <b>antes</b> de tocar el agregado: un archivo inexistente, ajeno o
/// sin subir es 422 <c>hr.file.not_found</c>, uno rechazado por el pipeline es 422 <c>hr.file.rejected</c>, y en ninguno se escribe nada.
/// </summary>
internal static class HrDocumentAttachment
{
    public static async Task<DocumentRef?> ResolveAsync(
        IHrDocumentStore store, Guid tenantId, Guid? fileId, CancellationToken cancellationToken)
    {
        if (fileId is not { } id)
        {
            return null;
        }

        var file = await store.CompleteAsync(tenantId, id, cancellationToken);
        return file.State switch
        {
            HrDocumentState.Available => new DocumentRef(id, file.FileName ?? string.Empty),
            HrDocumentState.Rejected => throw new HrDomainException(
                HrErrorCodes.FileRejected, "The file was rejected by the security checks. Upload another one."),
            _ => throw new HrDomainException(HrErrorCodes.FileNotFound, $"File '{id}' was not found or was not uploaded."),
        };
    }
}
