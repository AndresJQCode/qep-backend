namespace Modules.Storage.Application;

// Puerto de salida hacia Cloudflare R2 por su API compatible con S3 (ADR 0020).
// Las URLs prefirmadas son de vida corta y se emiten sólo tras (re)evaluar la autorización.
public interface IObjectStorage
{
    Task<Uri> CreatePresignedUploadUrlAsync(
        string key,
        string contentType,
        CancellationToken cancellationToken);

    // Con la vida por defecto de la configuracion. `downloadFileName` firma
    // `Content-Disposition: attachment` con ese nombre: sin el, el navegador abre el archivo en
    // vez de bajarlo -- un PDF o una imagen se renderizan inline, que es justo lo que una accion
    // llamada "descargar" no debe hacer. Null deja la disposicion al criterio del navegador,
    // para los enlaces que no consume una persona.
    Task<Uri> CreatePresignedDownloadUrlAsync(
        string key,
        string? downloadFileName,
        CancellationToken cancellationToken);

    // Igual que la anterior, pero con vida y nombre de archivo propios. Existe para los enlaces
    // que no consume un navegador ya abierto sino que viajan por correo: la expiración global es
    // de minutos, y el destinatario necesita además que el archivo baje con un nombre legible en
    // vez del identificador de la clave.
    Task<Uri> CreatePresignedDownloadUrlAsync(
        string key,
        TimeSpan expiry,
        string? downloadFileName,
        CancellationToken cancellationToken);

    // Metadata del objeto almacenado, o null si el objeto no está (la subida nunca ocurrió).
    Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task PromoteAsync(
        string sourceKey,
        string destinationKey,
        string expectedChecksum,
        CancellationToken cancellationToken);

    // Lectura/escritura del lado del servidor, para procesos de backend que necesitan los bytes
    // directo (por ejemplo un módulo parseando un archivo importado, o escribiendo un reporte
    // generado) en vez de entregarle una URL prefirmada a un navegador. Mismo bucket y mismas
    // credenciales que el camino de URL prefirmada; sólo otro patrón de acceso, sin navegador.
    Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken);

    Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken);

    // Spec 2026-10-09 §6.6: subida y lectura por stream para lo que no cabe en memoria (un documento de
    // WhatsApp llega a 100 MB). El largo viene de quien llama: con R2 la subida en chunks firmados falla,
    // así que hay que conocerlo antes de subir.
    Task UploadAsync(
        string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken);

    // null si el objeto no está. El stream es del llamador: lo cierra él.
    Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public sealed record StoredObject(long SizeBytes, string Checksum);

// No es un Stream: lleva uno, con su tipo y su largo. El nombre es el del contrato del spec 2026-10-09.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Holds a stream; the name is the spec contract.")]
public sealed record StoredObjectStream(Stream Content, string ContentType, long Length);
