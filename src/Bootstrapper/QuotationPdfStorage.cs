using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacenamiento de objetos de <c>Storage</c> al puerto que <c>quotations</c> declara
/// para guardar el PDF generado. Mismo criterio que <see cref="CustomerExportStorage"/>: ningún
/// módulo de negocio referencia al otro, y el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
///
/// El PDF **no** se registra como <c>FileResource</c>. Esa máquina de estados
/// —pendiente, subido, escaneado, disponible— existe para archivos que sube un cliente no
/// confiable: verifica tamaño, calcula checksum y pasa por antivirus. Un PDF que genera el
/// propio backend no necesita nada de eso, y hacerlo pasar por ahí sería forzar el flujo para
/// que encaje.
/// </summary>
internal sealed class QuotationPdfStorage(
    IObjectStorage objectStorage,
    IOptions<StorageOptions> options)
    : IQuotationPdfStorage
{
    private const string PdfContentType = "application/pdf";

    /// <summary>
    /// Prefijo propio, aparte de <c>staging/</c> y <c>files/</c>, por el mismo motivo que
    /// <c>exports/</c>: a estos objetos no los barre <c>StagingCleanupWorker</c> —que se guía por
    /// filas de <c>storage.file_resources</c>, y acá no se crea ninguna— sino una regla de
    /// lifecycle del bucket configurada sobre este prefijo.
    /// </summary>
    private const string QuotationPrefix = "quotations";

    public async Task<string> SaveAsync(
        Guid tenantId,
        QuotationId quotationId,
        byte[] content,
        CancellationToken cancellationToken)
    {
        // Clave nueva en cada generación, no una derivada de la cotización: regenerar no pisa el
        // objeto anterior, así que un enlace que alguien ya tenía abierto sigue sirviendo hasta
        // que la regla de lifecycle lo limpie. El id aleatorio ademas evita que se pueda adivinar
        // desde afuera.
        var key = $"{QuotationPrefix}/tenants/{tenantId:N}/" +
            $"{DateTime.UtcNow:yyyy/MM}/{Guid.CreateVersion7():N}.pdf";

        await objectStorage.UploadAsync(key, content, PdfContentType, cancellationToken);
        return key;
    }

    public async Task<string> CreateDownloadUrlAsync(
        string storageKey, string downloadFileName, CancellationToken cancellationToken)
    {
        // Minutos y no horas: a diferencia del PDF que baja Meta —que puede quedar encolado— esta
        // URL la consume un navegador que ya está en pantalla esperando la descarga.
        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            storageKey,
            TimeSpan.FromMinutes(options.Value.PresignedUrlMinutes),
            downloadFileName,
            cancellationToken);

        // AbsoluteUri, nunca ToString(): ToString() desescapa el query string y rompe la firma.
        return url.AbsoluteUri;
    }
}
