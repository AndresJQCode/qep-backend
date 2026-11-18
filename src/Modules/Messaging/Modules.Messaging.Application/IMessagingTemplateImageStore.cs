namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-17: la imagen fija del encabezado de una plantilla, guardada en Storage como archivo de dueño de sistema
/// (<c>FileOwnerType.MessageTemplate</c>, con el <c>tenantId</c> como dueño). Messaging no referencia Storage: el adaptador vive en
/// Bootstrapper. La autorización es de quien llama (<c>messaging.template.manage</c>); el puerto no mira permisos <c>storage.file.*</c>.
/// </summary>
public interface IMessagingTemplateImageStore
{
    /// <summary>Crea el archivo pendiente con tope de <see cref="TemplateHeaderImagePolicy.MaxSizeBytes"/> y firma el PUT.</summary>
    Task<TemplateImageUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken);

    /// <summary>Completa la subida (idempotente) y dice cómo quedó el archivo. Uno de otro tenant u otro dueño, o un id que no existe, es
    /// <see cref="TemplateImageState.NotFound"/>.</summary>
    Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Los bytes del archivo, para subirlo a Meta como ejemplo del encabezado.</summary>
    Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Lo publica en el bucket público y devuelve la URL pública absoluta.</summary>
    Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Lo despublica y lo borra lógicamente. Idempotente: un archivo que no existe, ya borrado o ajeno no hace nada.</summary>
    Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}

/// <summary>La sesión abierta. <see cref="Headers"/> son las cabeceras que firmó el PUT; el navegador las manda tal cual.</summary>
public sealed record TemplateImageUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);

public enum TemplateImageState
{
    Available,
    NotFound,
    NotUploaded,
    Rejected,
}

/// <summary>Cómo quedó el archivo al completar. <see cref="MimeType"/> y <see cref="SizeBytes"/> sólo valen con
/// <see cref="TemplateImageState.Available"/>.</summary>
public sealed record TemplateImageFile(TemplateImageState State, string? MimeType, long SizeBytes);
