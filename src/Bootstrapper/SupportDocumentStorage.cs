using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Fase 4b (H8): Storage → Soporte, el PDF de la respuesta formal en el bucket privado. Calco de <see cref="QuotationPdfStorage"/>, con
/// una diferencia que importa: el prefijo es <c>support/</c> y no <c>quotations/</c>, porque el de cotizaciones tiene regla de ciclo de
/// vida y este documento es prueba legal (10 años, spec §13.10). No se registra como <c>FileResource</c>: lo genera el backend, no lo
/// sube nadie, así que no pasa por escaneo ni por la máquina de estados de Storage.
/// </summary>
internal sealed partial class SupportDocumentStorage(
    IObjectStorage objectStorage, IClock clock, ILogger<SupportDocumentStorage> logger) : ISupportDocumentStorage
{
    private const string PdfContentType = "application/pdf";

    /// <summary>Sin regla de ciclo de vida en el bucket: lo que entra acá no se borra solo.</summary>
    private const string SupportPrefix = "support";

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not delete the formal reply document {StorageKey}; the object stays orphaned in the private bucket.")]
    private static partial void LogDeleteFailed(ILogger logger, string storageKey, Exception exception);

    public async Task<string> SaveAsync(Guid tenantId, byte[] content, CancellationToken cancellationToken)
    {
        // Clave nueva y aleatoria en cada documento: no se deriva del ticket ni de la respuesta, así que no se adivina desde afuera y
        // un documento nunca pisa a otro.
        var now = clock.UtcNow;
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{SupportPrefix}/tenants/{tenantId:N}/replies/{now:yyyy}/{now:MM}/{Guid.CreateVersion7():N}.pdf");

        await objectStorage.UploadAsync(key, content, PdfContentType, cancellationToken);
        return key;
    }

    public Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken) =>
        objectStorage.DownloadAsync(storageKey, cancellationToken);

    public Task<Uri> CreateDownloadUrlAsync(
        string storageKey, string fileName, TimeSpan lifetime, CancellationToken cancellationToken) =>
        objectStorage.CreatePresignedDownloadUrlAsync(storageKey, lifetime, fileName, cancellationToken);

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            await objectStorage.DeleteAsync(storageKey, cancellationToken);
        }
        catch (Exception exception)
        {
            // Best effort (H7): limpia el objeto de un documento que no llegó a guardarse. Si falla, un objeto huérfano no rompe nada y
            // quien llama ya está respondiendo otro error; lanzar acá lo taparía.
            LogDeleteFailed(logger, storageKey, exception);
        }
    }
}
