using Microsoft.Extensions.Options;
using Modules.Customers.Application;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>customers</c> declara para
/// dejar ahi el Excel exportado. Mismo criterio que <see cref="QuotationFileLookup"/> y
/// <c>ProductImageLookup</c>: ningun modulo de negocio referencia al otro, y el composition root
/// —que ya referencia a los dos— es el unico lugar donde ese acoplamiento es legitimo.
///
/// El archivo **no** se registra como <c>FileResource</c>: no es un adjunto que alguien administre
/// desde la UI de archivos, es un artefacto temporal que se descarga una vez y se vence. Por eso va
/// directo al objeto, con su propio prefijo.
/// </summary>
internal sealed class CustomerExportStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options)
    : ICustomerExportStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// El prefijo de la carpeta temporal. Aparte de <c>staging/</c> y de <c>files/</c> porque su
    /// ciclo de vida es otro: estos objetos no los barre <c>StagingCleanupWorker</c> —que se guia
    /// por filas de <c>storage.file_resources</c>, y una exportacion no crea ninguna— sino una
    /// regla de lifecycle del bucket en Cloudflare, configurada sobre este prefijo.
    /// </summary>
    private const string ExportPrefix = "exports";

    public async Task<CustomerExportUpload> UploadAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        // La clave la arma este adaptador y no StorageKey: esa clase es internal de
        // Modules.Storage.Application y sus formas son las de FileResource, que este camino no usa.
        // El identificador aleatorio evita que dos exportaciones del mismo tenant en el mismo
        // segundo se pisen, y que el nombre de un objeto se pueda adivinar desde afuera.
        var key = $"{ExportPrefix}/tenants/{tenantId:N}/" +
            $"{DateTime.UtcNow:yyyy/MM}/{Guid.CreateVersion7():N}.xlsx";

        await objectStorage.UploadAsync(key, content, ExcelContentType, cancellationToken);

        var expiry = TimeSpan.FromHours(options.Value.ExportUrlHours);
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            key, expiry, fileName, cancellationToken);

        return new CustomerExportUpload(url.ToString(), DateTimeOffset.UtcNow.Add(expiry));
    }
}
