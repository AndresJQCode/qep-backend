using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de Storage al puerto de exportaciones de Quotations. Calcado
/// de <see cref="CustomerExportStorage"/>, con una diferencia a propósito: la clave lleva el id del
/// job y no un identificador aleatorio, así que un reintento pisa el mismo objeto (D9). Sigue sin
/// ser adivinable desde afuera: el id es un UUID v7 que sólo conocen la tabla y el correo.
///
/// El objeto va bajo `exports/` a propósito: es el prefijo de la regla de lifecycle que ya existe
/// en el bucket privado (`expire-exports`, README § Reportes exportados), y una clave fuera de él
/// no la borraría nadie. La vigencia es `Storage:ExportUrlHours`, la misma opción que lee
/// CustomerExportStorage: no hay una vigencia propia de estos exports.
/// </summary>
internal sealed class ExportFileStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options,
    IClock clock)
    : IExportFileStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    internal static string KeyFor(Guid tenantId, Guid jobId) =>
        $"exports/tenants/{tenantId:N}/jobs/{jobId:N}.xlsx";

    public async Task<ExportFileUpload> UploadAsync(
        Guid tenantId,
        Guid jobId,
        string fileName,
        string filePath,
        CancellationToken cancellationToken)
    {
        var key = KeyFor(tenantId, jobId);

        // A bytes porque IObjectStorage sólo sube byte[]. Es el .xlsx comprimido —órdenes de
        // magnitud menos que el grafo de celdas que armaba ClosedXML—, no las filas.
        var content = await File.ReadAllBytesAsync(filePath, cancellationToken);
        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            key, expiry, fileName, cancellationToken);

        // `AbsoluteUri`, nunca `ToString()`: ToString desescapa el query string y rompe la firma
        // (ver CustomerExportStorage).
        return new ExportFileUpload(url.AbsoluteUri, clock.UtcNow.Add(expiry));
    }
}
