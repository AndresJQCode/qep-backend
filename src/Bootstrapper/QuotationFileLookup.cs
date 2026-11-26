using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de archivos de <c>Storage</c> al puerto que <c>quotations</c> declara,
/// para el PDF de una cotización (US-12) y para los comprobantes de pago de un pedido (US-14).
/// Mismo criterio que <c>ProductImageLookup</c> entre Catalog y Storage (CAT-05) y
/// <c>QuotationCustomerLookup</c>/<c>QuotationProductPricingLookup</c>: ningún módulo de negocio
/// referencia al otro, y el composition root — que ya referencia a los dos — es el único lugar
/// donde ese acoplamiento es legítimo.
///
/// No decide las reglas (PDF vs. comprobante, tamaño máximo, tenant, disponibilidad): son de
/// <c>OrderPaymentProofResolver</c>, en Application. Sólo traduce qué significa "disponible" en
/// Storage, y desde el spec 2026-09-16 (D16) un comprobante ya movido no lo está.
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
        // Un archivo de dueño de sistema (adjunto de Soporte) o que pide un permiso extra (export de WMS) no existe para Quotations:
        // no se adjunta a un pedido (FileOwnerModules.IsReferenceableByOtherModules).
        return resource is null || !FileOwnerModules.IsReferenceableByOtherModules(resource.OwnerType)
            ? null
            : ToRef(resource);
    }

    // Una sola lectura (id = ANY) para los comprobantes de un cuerpo. ListByIdsAsync no filtra por tenant, así que el filtro va
    // acá, sobre lo que ya llegó en ese mismo viaje: otro tenant no viene, como un archivo que no existe.
    public async Task<IReadOnlyDictionary<Guid, QuotationFileRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationFileRef>();
        }

        var resources = await repository.ListByIdsAsync(
            fileIds.Distinct().Select(fileId => new FileResourceId(fileId)).ToArray(), cancellationToken);
        return resources
            .Where(resource => resource.TenantId == tenantId && FileOwnerModules.IsReferenceableByOtherModules(resource.OwnerType))
            .ToDictionary(resource => resource.Id.Value, ToRef);
    }

    private static QuotationFileRef ToRef(FileResource resource) =>
        new(
            resource.Id.Value,
            resource.TenantId,
            resource.MimeType,
            resource.SizeBytes,
            IsAvailableToAttach(resource),
            resource.OwnerType == FileOwnerType.PaymentProof);

    // Spec 2026-09-16, D16: un PaymentProof con PublicStorageKey ya se movió al bucket público y su
    // temporal se borró, así que PublicPaymentProofPublisher no tendría de dónde copiarlo para otro
    // pedido. Sólo un PaymentProof: una imagen User publicada conserva su original privado (D13).
    private static bool IsAvailableToAttach(FileResource resource) =>
        resource.Status == FileResourceStatus.Available
        && !(resource.OwnerType == FileOwnerType.PaymentProof && resource.PublicStorageKey is not null);

    public async Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");

        // Mismo código para "no existe" y "es de otro tenant": distinguirlos confirmaría que el
        // id existe en otro tenant, justo lo que la frontera esconde.
        if (resource.TenantId != tenantId || !FileOwnerModules.IsReferenceableByOtherModules(resource.OwnerType))
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

        // `AbsoluteUri`, nunca `ToString()`: `Uri.ToString()` devuelve la forma legible y
        // **desescapa** el query string (`%20` vuelve a ser espacio, `%22` comilla), lo que
        // rompe la firma para cualquier cliente HTTP estricto. Un navegador lo normaliza y no
        // se nota; Meta, que baja este archivo para adjuntarlo al WhatsApp, lo rechaza.
        return url.AbsoluteUri;
    }
}
