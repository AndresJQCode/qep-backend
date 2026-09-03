using Microsoft.Extensions.Options;
using Modules.Catalog.Application;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>catalog</c> declara para
/// dejar ahi el Excel exportado. Gemelo de <see cref="CustomerExportStorage"/>, y por el mismo
/// motivo vive aca: ningun modulo de negocio referencia al otro, y el composition root es el unico
/// lugar donde ese acoplamiento es legitimo.
///
/// El archivo **no** se registra como <c>FileResource</c>: no es un adjunto que alguien administre
/// desde la UI de archivos, es un artefacto temporal que se descarga una vez y se vence.
/// </summary>
internal sealed class ProductExportStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options)
    : IProductExportStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Mismo prefijo que la exportacion de clientes: estos objetos no los barre
    /// <c>StagingCleanupWorker</c> —que se guia por filas de <c>storage.file_resources</c>, y una
    /// exportacion no crea ninguna— sino la regla de lifecycle del bucket configurada sobre este
    /// prefijo. Compartirlo es lo que hace que esa regla cubra las dos exportaciones.
    /// </summary>
    private const string ExportPrefix = "exports";

    public async Task<ProductExportUpload> UploadAsync(
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

        return new ProductExportUpload(url.ToString(), DateTimeOffset.UtcNow.Add(expiry));
    }
}
