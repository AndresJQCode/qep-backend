namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia el módulo Storage. Sirve dos usos del mismo módulo: el PDF de la cotización
/// (US-12: se sube ahí como cualquier otro archivo, con el flujo de carga firmada que Storage ya
/// expone — no hay generación de PDF en el backend) y los comprobantes de pago de una venta
/// (US-14). Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/> — el
/// adaptador vive en <c>Bootstrapper</c>, igual que <c>ProductImageLookup</c> entre Catalog y
/// Storage (CAT-05).
/// </summary>
public interface IQuotationFileLookup
{
    Task<QuotationFileRef?> FindAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// URL de descarga directa del archivo, para entregársela a un tercero que no tiene sesión
    /// en el sistema — hoy WhatsApp, que descarga el PDF de la cotización desde los servidores
    /// de Meta (<c>SendQuotation</c>).
    ///
    /// Vive de horas y no de los minutos de <c>Storage:PresignedUrlMinutes</c>, mismo criterio
    /// que <c>ExportUrlHours</c>: aquellas URLs las consume un navegador que ya está en
    /// pantalla, y ésta viaja por fuera hasta un destinatario que puede tardar.
    /// <paramref name="downloadFileName"/> es el nombre con el que baja el archivo — la clave
    /// de almacenamiento no le dice nada a quien lo recibe.
    /// </summary>
    Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken);
}

public sealed record QuotationFileRef(
    Guid FileId, Guid TenantId, string MimeType, long SizeBytes, bool IsAvailable);
