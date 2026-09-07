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
    /// Copia el PDF al bucket público y devuelve su URL, sin firma ni query string.
    ///
    /// Existe porque **Meta no puede descargar el PDF desde una URL prefirmada de R2**: la baja
    /// con un cliente HTTP propio para adjuntarla al mensaje, le falla, y descarta el mensaje
    /// entero minutos después de que Zenvia ya respondió 200. Verificado el 2026-09-06 con el
    /// mismo PDF y la misma plantilla: con URL pública llega, con la firmada no.
    ///
    /// La clave de la copia es **nueva en cada publicación**. Eso hace dos cosas a la vez: la
    /// URL no se puede adivinar desde el id de la cotización, y Meta --que cachea el documento
    /// por URL-- vuelve a descargarlo en un reenvío en vez de entregar la versión vieja.
    ///
    /// La copia es efímera: la limpia una regla de lifecycle del bucket sobre el prefijo
    /// `quotations/`. El canónico sigue siendo el objeto privado.
    /// </summary>
    Task<string> PublishAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Una URL firmada de vida corta contra el bucket privado. La descarga va directo de R2 al
    /// navegador, sin pasar por la API: firmar es lo único que hace el backend.
    /// </summary>
    Task<string> CreateDownloadUrlAsync(
        string storageKey, string downloadFileName, CancellationToken cancellationToken);
}
