using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;
using Modules.Suppliers.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>suppliers</c> declara para
/// dejar ahí el Excel exportado. Mismo criterio que <see cref="CustomerExportStorage"/>: ningún
/// módulo de negocio referencia al otro, y el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
///
/// El archivo **no** se registra como <c>FileResource</c>: es un artefacto temporal que se descarga
/// una vez y se vence, así que va directo al objeto bajo el prefijo <c>exports/</c>, cuyo ciclo de
/// vida lo maneja una regla del bucket y no <c>StagingCleanupWorker</c>.
/// </summary>
internal sealed class SupplierExportStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options)
    : ISupplierExportStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string ExportPrefix = "exports";

    public async Task<SupplierExportUpload> UploadAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        // El identificador aleatorio evita que dos exportaciones del mismo tenant en el mismo
        // segundo se pisen, y que el nombre de un objeto se pueda adivinar desde afuera.
        var key = $"{ExportPrefix}/tenants/{tenantId:N}/" +
            $"{DateTime.UtcNow:yyyy/MM}/{Guid.CreateVersion7():N}.xlsx";

        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            key, expiry, fileName, cancellationToken);

        // `AbsoluteUri`, nunca `ToString()`: este último desescapa el query string y rompe la firma
        // para cualquier cliente HTTP estricto.
        return new SupplierExportUpload(url.AbsoluteUri, DateTimeOffset.UtcNow.Add(expiry));
    }
}
