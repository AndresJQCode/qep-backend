using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve el <c>pdfFileId</c> que llega en el comando de envío (US-12) contra los archivos
/// **del tenant de la cotización**. Mismo criterio que <c>ProductImageResolver</c> en Catalog:
/// la referencia es blanda, sin FK que la respalde, así que esta comprobación es la única red.
/// </summary>
internal static class QuotationPdfResolver
{
    public static async Task ResolveAsync(
        IQuotationFileLookup lookup,
        Guid tenantId,
        Guid pdfFileId,
        CancellationToken cancellationToken)
    {
        var file = await lookup.FindAsync(tenantId, pdfFileId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant": distinguirlos confirmaría que el
        // id existe en otro tenant, justo lo que la frontera esconde.
        if (file is null || file.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.pdf_not_found",
                $"File '{pdfFileId}' was not found in this tenant.");
        }

        if (!file.IsAvailable)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.pdf_not_available",
                "The PDF file has not finished uploading yet.");
        }

        if (!string.Equals(file.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.pdf_not_a_pdf",
                "The file assigned as the quotation PDF is not a PDF.");
        }
    }
}
