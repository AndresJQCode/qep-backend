using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure;
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
internal sealed class QuotationFileLookup(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IOptions<QuotationsOptions> options) : IQuotationFileLookup
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

    public async Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");

        // Mismo código para "no existe" y "es de otro tenant": distinguirlos confirmaría que el
        // id existe en otro tenant, justo lo que la frontera esconde.
        if (resource.TenantId != tenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        resource.EnsureDownloadable();

        var url = await objectStorage.CreatePresignedDownloadUrlAsync(
            resource.StorageKey,
            TimeSpan.FromHours(options.Value.WhatsApp.DocumentUrlHours),
            downloadFileName,
            cancellationToken);

        return url.ToString();
    }
}
