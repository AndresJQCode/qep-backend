using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve cada archivo de comprobante de pago que llega en el comando de conversión (US-14)
/// contra los archivos **del tenant de la cotización**. Mismo criterio que
/// <c>QuotationPdfResolver</c>/<c>ProductImageResolver</c>: la referencia es blanda, sin FK que
/// la respalde, así que esta comprobación es la única red.
/// </summary>
internal static class SalePaymentProofResolver
{
    // US-14: "Acepta PDF, JPG, PNG, hasta 10 MB por archivo".
    private static readonly string[] AllowedMimeTypes =
        ["application/pdf", "image/jpeg", "image/png"];

    private const long MaxSizeBytes = 10 * 1024 * 1024;

    public static async Task ResolveAsync(
        IQuotationFileLookup lookup,
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await lookup.FindAsync(tenantId, fileId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant" -- la frontera de tenant no se
        // distingue desde afuera.
        if (file is null || file.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.file_not_found",
                $"File '{fileId}' was not found in this tenant.");
        }

        if (!file.IsAvailable)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.file_not_available",
                "The payment proof file has not finished uploading yet.");
        }

        if (!AllowedMimeTypes.Contains(file.MimeType, StringComparer.OrdinalIgnoreCase))
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.file_type_not_allowed",
                "The payment proof must be a PDF, JPG or PNG file.");
        }

        if (file.SizeBytes > MaxSizeBytes)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.file_too_large",
                "The payment proof cannot exceed 10 MB.");
        }
    }
}
