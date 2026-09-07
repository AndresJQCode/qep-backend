using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda el PDF generado y produce el enlace con el que se descarga. Lo implementa el
/// composition root sobre <c>Storage</c>, igual que <c>IQuotationFileLookup</c>: este módulo no
/// conoce R2 ni sabe firmar nada.
///
/// El objeto vive en el bucket **privado**, que es el canónico. La copia pública que se le
/// entrega a Meta al enviar por WhatsApp se crea aparte y es descartable.
/// </summary>
public interface IQuotationPdfStorage
{
    /// <summary>Sube el PDF y devuelve su clave. La clave la arma la implementación --lleva el
    /// prefijo `quotations/` para que la regla de lifecycle del bucket pueda agruparlos-- y es
    /// lo único que este módulo guarda.</summary>
    Task<string> SaveAsync(
        Guid tenantId, QuotationId quotationId, byte[] content, CancellationToken cancellationToken);

    /// <summary>
    /// Una URL firmada de vida corta contra el bucket privado. La descarga va directo de R2 al
    /// navegador, sin pasar por la API: firmar es lo único que hace el backend.
    /// </summary>
    Task<string> CreateDownloadUrlAsync(
        string storageKey, string downloadFileName, CancellationToken cancellationToken);
}
