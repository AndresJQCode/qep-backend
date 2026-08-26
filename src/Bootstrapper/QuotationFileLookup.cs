using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de archivos de <c>Storage</c> al puerto que <c>quotations</c> declara,
/// para el PDF de una cotización (US-12) y para los comprobantes de pago de una venta (US-14).
/// Mismo criterio que <c>ProductImageLookup</c> entre Catalog y Storage (CAT-05) y
/// <c>QuotationCustomerLookup</c>/<c>QuotationProductPricingLookup</c>: ningún módulo de negocio
/// referencia al otro, y el composition root — que ya referencia a los dos — es el único lugar
/// donde ese acoplamiento es legítimo.
///
/// No decide nada: las reglas (PDF vs. comprobante, tamaño máximo, tenant, disponibilidad) son
/// de <c>QuotationPdfResolver</c>/<c>SalePaymentProofResolver</c>, en Application.
/// </summary>
internal sealed class QuotationFileLookup(IFileResourceRepository repository) : IQuotationFileLookup
{
    public async Task<QuotationFileRef?> FindAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        return resource is null
            ? null
            : new QuotationFileRef(
                resource.Id.Value,
                resource.TenantId,
                resource.MimeType,
                resource.SizeBytes,
                resource.Status == FileResourceStatus.Available);
    }
}
