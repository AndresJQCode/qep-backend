namespace Modules.Storage.Application;

// Puerto de salida hacia Cloudflare R2 por su API compatible con S3 (ADR 0020).
// Las URLs prefirmadas son de vida corta y se emiten sólo tras (re)evaluar la autorización.
public interface IObjectStorage
{
    Task<Uri> CreatePresignedUploadUrlAsync(
        string key,
        string contentType,
        CancellationToken cancellationToken);

    Task<Uri> CreatePresignedDownloadUrlAsync(
        string key,
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
}

public sealed record StoredObject(long SizeBytes, string Checksum);
