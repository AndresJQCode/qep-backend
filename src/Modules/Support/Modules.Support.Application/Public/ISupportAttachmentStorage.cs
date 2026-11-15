namespace Modules.Support.Application;

/// <summary>
/// Los archivos de los adjuntos del formulario público (plan F30 a F32). Viven en Storage, que Support no ve: el adaptador está en
/// Bootstrapper (<c>SupportAttachmentStorage</c>) y usa la subida sin miembro de Storage, con el dueño <c>SupportTicket</c>. El
/// <c>uploadKey</c> es el secreto que ata la subida a quien la abrió: lo genera Soporte, sólo lo recibe esa persona y
/// cada operación lo exige. Un archivo de otro tenant o con otra clave responde igual que uno que no existe.
/// </summary>
public interface ISupportAttachmentStorage
{
    /// <summary>Abre la sesión. Lanza <c>SupportDomainException</c> con <c>support.public.attachment_invalid</c> si Storage no
    /// acepta la declaración (el validador de Soporte ya filtró casi todo).</summary>
    Task<SupportAttachmentUpload> OpenAsync(
        Guid tenantId, Guid uploadKey, string name, string mimeType, long sizeBytes, CancellationToken cancellationToken);

    Task<SupportAttachmentCompletion> CompleteAsync(Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken);

    /// <summary>El archivo si es del tenant, con esa clave y <c>Available</c>; si no, <c>null</c>.</summary>
    Task<SupportAttachmentFile?> FindAvailableAsync(Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken);

    /// <summary>Una URL firmada de 5 minutos (plan F37). Quien llama ya autorizó.</summary>
    Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}

/// <summary>La sesión de subida: los headers van firmados y el navegador los tiene que mandar tal cual en el PUT.</summary>
public sealed record SupportAttachmentUpload(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);

public enum SupportAttachmentCompletion
{
    Available,
    Rejected,
    NotFound,
    NotUploaded,
}

public sealed record SupportAttachmentFile(Guid FileId, string Name, string MimeType, long SizeBytes);
