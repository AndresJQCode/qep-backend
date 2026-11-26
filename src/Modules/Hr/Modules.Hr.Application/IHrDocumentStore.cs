namespace Modules.Hr.Application;

/// <summary>La sesión de subida abierta: el navegador hace el <c>PUT</c> con exactamente <see cref="Headers"/> (van firmados).</summary>
public sealed record HrUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);

public enum HrDocumentState
{
    Available,
    NotFound,
    NotUploaded,
    Rejected,
}

/// <param name="FileName">El nombre que Storage guardó; sólo con <see cref="HrDocumentState.Available"/>.</param>
public sealed record HrDocumentFile(HrDocumentState State, string? FileName);

/// <summary>El PDF que acepta Hr: un contrato firmado, un otrosí, una carta de prórroga. Nada más, y no más de 10 MB.</summary>
public static class HrDocumentPolicy
{
    public const long MaxSizeBytes = 10_485_760;

    public const string MimeType = "application/pdf";

    /// <summary>Un nombre con <c>.pdf</c>, el tipo PDF y de 1 byte a 10 MB. Lo revisan el caso de uso y el adaptador, porque la tabla
    /// general de Storage también admite imágenes y documentos de Office.</summary>
    public static bool IsValidDeclaration(string? fileName, string? mimeType, long sizeBytes) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Trim().EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && string.Equals(mimeType?.Trim(), MimeType, StringComparison.OrdinalIgnoreCase)
        && sizeBytes is > 0 and <= MaxSizeBytes;
}

/// <summary>
/// El PDF de un contrato o de un registro, sobre Storage (dueño <c>HrDocument</c>, <c>ownerId = tenantId</c>). Hr no referencia
/// Storage: el adaptador vive en Bootstrapper. Todo método filtra por tenant: un id ajeno es el mismo «no existe».
/// </summary>
public interface IHrDocumentStore
{
    /// <summary>Lanza <c>HrDomainException(hr.file.invalid)</c> si Storage rechaza la declaración.</summary>
    Task<HrUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken);

    /// <summary>Completa la subida; es idempotente. El resultado dice si el archivo quedó disponible.</summary>
    Task<HrDocumentFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>La URL firmada de 5 minutos; <c>null</c> si no es un <c>HrDocument</c> disponible del tenant.</summary>
    Task<Uri?> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
