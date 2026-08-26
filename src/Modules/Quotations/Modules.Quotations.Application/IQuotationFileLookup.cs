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
}

public sealed record QuotationFileRef(
    Guid FileId, Guid TenantId, string MimeType, long SizeBytes, bool IsAvailable);
