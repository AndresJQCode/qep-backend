namespace Modules.Quotations.Application;

/// <summary>
/// Sube el Excel de una exportación y firma su enlace de descarga (D9). Puerto en Application y
/// adaptador en el composition root, igual que <c>ICustomerExportStorage</c>: Quotations no puede
/// referenciar Storage (QuotationsLayerTests lo impide).
///
/// Recibe la ruta del temporal y no los bytes: el writer arma el Excel en streaming a disco para
/// no tener las filas en memoria, y si algún día Storage acepta un stream, el cambio queda en el
/// adaptador —hoy lo lee entero, pero ya comprimido—.
/// </summary>
public interface IExportFileStorage
{
    Task<ExportFileUpload> UploadAsync(
        Guid tenantId,
        Guid jobId,
        string fileName,
        string filePath,
        CancellationToken cancellationToken);
}

/// <summary>El enlace y hasta cuándo sirve: el correo tiene que decir el vencimiento.</summary>
public sealed record ExportFileUpload(string DownloadUrl, DateTimeOffset ExpiresAt);
