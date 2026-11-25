using BuildingBlocks.Application.RichText;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Genera el PDF de un pedido y lo guarda en el bucket privado; devuelve la clave. Mismo documento que
/// el de la cotización (<see cref="QuotationPdfDocumentMapper.FromOrder"/>), mismo renderer y mismo
/// logo.
/// </summary>
public interface IOrderPdfProvider
{
    Task<string> RenderAndSaveAsync(
        Guid tenantId, OrderId orderId, OrderDetailResponse detail, CancellationToken cancellationToken);
}

/// <summary>
/// A diferencia de <see cref="QuotationPdfProvider"/>, **no cachea**: genera en cada envío y en cada
/// «Descargar PDF» (<see cref="ExportOrderPdfHandler"/>, spec 2026-11-16). El caché de la cotización es
/// una tabla (<c>quotations.quotation_pdfs</c>) invalidada por <c>Quotation.Version</c>; el del pedido
/// pediría una migración y la misma invalidación por <c>Order.Version</c>. Mientras una descarga por
/// clic no se note en latencia ni en costo de <c>qcode-pdf</c>, esa maquinaria no se paga.
/// </summary>
public sealed class OrderPdfProvider(
    IQuotationPdfRenderer renderer,
    IQuotationPdfStorage storage,
    ITenantClock tenantClock,
    IQuotationLogoLookup logoLookup,
    ITenantDirectory tenantDirectory,
    IPdfContentStore pdfContentStore,
    IRichTextReader richTextReader)
    : IOrderPdfProvider
{
    public async Task<string> RenderAndSaveAsync(
        Guid tenantId, OrderId orderId, OrderDetailResponse detail, CancellationToken cancellationToken)
    {
        // La emisión se imprime en el día del tenant (spec 2026-09-17, punto 7), como en la cotización.
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var logo = await logoLookup.FindAsync(tenantId, cancellationToken);
        var regional = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var logoContent = logo is null ? null : await logoLookup.ReadAsync(logo, cancellationToken);
        // Un logo que no se pudo leer no frena el envío: el documento sale sin imagen en el membrete,
        // igual que el de la cotización.
        var pdfLogo = logo is null || logoContent is null
            ? null
            : new QuotationPdfLogo("logo" + logo.Extension, logoContent);

        // Sin caché no hay huella que guardar: las secciones se leen en cada envío (spec 2026-11-16 §5.4).
        var pdfContent = await pdfContentStore.GetAsync(tenantId, cancellationToken);
        var mapped = QuotationPdfDocumentMapper.FromOrder(detail, calendar, pdfLogo, regional?.NumberFormat, regional?.Language);
        var content = await renderer.RenderAsync(
            mapped with { Sections = QuotationPdfSectionsBuilder.Build(pdfContent, mapped, richTextReader) }, cancellationToken);
        return await storage.SaveOrderAsync(tenantId, orderId, content, cancellationToken);
    }
}
