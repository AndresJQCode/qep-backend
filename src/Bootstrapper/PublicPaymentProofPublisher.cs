using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Publica un comprobante de pago copiándolo al bucket público de R2, con
/// <c>Quotations:PaymentProofs:PublicLinks</c> encendida (spec 2026-09-15, P5). Mismo criterio que
/// <see cref="QuotationPdfStorage.PublishAsync"/>: clave nueva y aleatoria, que no se deduce de
/// ningún id que viaje en el navegador.
///
/// No pasa por <c>FileResource.Publish</c> a propósito (P6): esa regla —sólo imágenes— protege el
/// endpoint de publicación de Storage, y relajarla dejaría a cualquiera con <c>FilePublish</c>
/// publicar un PDF desde la API. Por lo mismo, para un comprobante <c>User</c> (v1) el
/// <c>FileResource</c> no se entera de esta copia: si alguien lo borra (<c>SoftDeleteFileHandler</c>),
/// la copia pública queda en el bucket. Un <c>PaymentProof</c> (spec 2026-09-16) sí: Storage registra
/// la clave cuando lo mueve (D10), y no deja borrarlo ni despublicarlo mientras un pedido lo
/// referencie (D15). Desde D19, reemplazar o quitar un comprobante de un pedido hace que Storage borre
/// la copia de ese adjunto y, en un <c>PaymentProof</c> que nadie más usa, el archivo entero: este
/// publicador sólo borra sus copias en el rollback de un request que falló.
/// </summary>
internal sealed class PublicPaymentProofPublisher(
    IFileResourceRepository repository,
    IPublicObjectStorage publicObjectStorage) : IPaymentProofPublisher
{
    /// <summary>
    /// Prefijo propio en el bucket público, aparte de <c>quotations/</c>: sobre éste **no debe haber
    /// regla de lifecycle**. El de los PDF de cotización puede tener una; confundirlos borraría
    /// comprobantes cuyos enlaces siguen en Excels ya enviados.
    /// </summary>
    private const string Prefix = "payment-proofs";

    // La extensión sale del MimeType y no del nombre, que puede traer ".jpeg", mayúsculas o nada. Son
    // los tipos que OrderPaymentProofResolver deja pasar; WebP es la imagen procesada de un
    // comprobante v2 (spec 2026-09-16, D7).
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
        };

    public async Task<string?> PublishAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);

        // OrderPaymentProofResolver ya lo validó antes de llegar acá, pero la frontera de tenant se
        // revisa igual, con sus mismos códigos: "no existe" y "es de otro tenant" no se distinguen
        // desde afuera.
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_found",
                $"File '{fileId}' was not found in this tenant.");
        }

        if (resource.Status != FileResourceStatus.Available)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_available",
                "The payment proof file has not finished uploading yet.");
        }

        var publicKey = $"{Prefix}/{Guid.CreateVersion7():N}{ExtensionOf(resource.MimeType)}";
        await publicObjectStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
        return publicKey;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        publicObjectStorage.DeleteAsync(publicKey, cancellationToken);

    public string? UrlFor(string publicKey) => publicObjectStorage.GetUrl(publicKey);

    private static string ExtensionOf(string mimeType) =>
        ExtensionsByMimeType.TryGetValue(mimeType, out var extension)
            ? extension
            : throw new QuotationsDomainException(
                "order.payment_proof.file_type_not_allowed",
                "The payment proof must be a PDF, JPG, PNG or WEBP file.");
}
