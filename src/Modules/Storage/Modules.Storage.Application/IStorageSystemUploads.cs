using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// Subidas sin miembro (Soporte fase 3, plan F30 a F32): el formulario público de Soporte sube adjuntos por el mismo pipeline de
/// Storage que la consola —staging, magic bytes, antivirus, cuarentena, promoción y barrido—, pero sin <c>IExecutionContext</c> y sin
/// <c>StorageAuthorization</c>, que siguen siendo sólo de la consola. Lo que ata la sesión a quien la abrió es el par
/// <c>(ownerType, ownerId)</c>: el módulo que llama pone en <c>ownerId</c> un secreto aleatorio que sólo entrega a quien abrió la sesión,
/// y cada operación lo exige. Un par que no casa responde igual que un id que no existe. Lo usa sólo código de confianza del
/// composition root (un adaptador de Bootstrapper), nunca un endpoint de Storage.
/// </summary>
public interface IStorageSystemUploads
{
    /// <summary>Valida la declaración con <see cref="FileUploadPolicy"/> y el tope de quien llama (nunca mayor al general), crea el
    /// recurso <c>PendingUpload</c> y firma el PUT. Lanza <c>StorageDomainException</c> si la declaración no pasa.</summary>
    Task<SystemUploadSession> OpenAsync(
        Guid tenantId,
        FileOwnerType ownerType,
        Guid ownerId,
        string name,
        string mimeType,
        long sizeBytes,
        long maxSizeBytes,
        CancellationToken cancellationToken);

    /// <summary>Completa la subida con las mismas verificaciones que la consola, en el mismo orden. Nunca lanza por el contenido: lo
    /// rechazado vuelve como <see cref="SystemUploadOutcome.Rejected"/>, ya en cuarentena.</summary>
    Task<SystemUploadResult> CompleteAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>El archivo si existe en el tenant, es de ese dueño y está <c>Available</c>; si no, <c>null</c>.</summary>
    Task<SystemFileInfo?> FindAvailableAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Una URL firmada de 5 minutos que baja el archivo con su nombre. Sin auditoría de Storage: la autorización y la
    /// auditoría son de quien llama. Un archivo de otro tenant, que no está <c>Available</c> o cuyo dueño no es del sistema
    /// (<see cref="Modules.Storage.Domain.SystemOwnedFiles"/>) es 404.</summary>
    Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}

/// <summary>La sesión abierta. <see cref="Headers"/> son los que el navegador tiene que mandar en el PUT: van firmados.</summary>
public sealed record SystemUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);

public enum SystemUploadOutcome
{
    Available,
    Rejected,
    NotFound,
    ObjectMissing,
}

public sealed record SystemUploadResult(SystemUploadOutcome Outcome);

public sealed record SystemFileInfo(Guid FileId, string Name, string MimeType, long SizeBytes);
