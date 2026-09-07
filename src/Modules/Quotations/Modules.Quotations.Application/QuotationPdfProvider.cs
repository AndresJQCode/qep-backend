using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Deja el PDF de una cotización al día y devuelve el registro. Lo comparten exportar y enviar
/// porque los dos necesitan exactamente lo mismo, y duplicar esa decisión terminaría con uno de
/// los dos regenerando de más --o de menos-- sin que nadie lo note.
///
/// No guarda: agrega la fila al repositorio cuando es nueva y muta la existente cuando no, pero
/// el <c>SaveChangesAsync</c> lo hace el caso de uso, que es el dueño de la transacción.
/// </summary>
public interface IQuotationPdfProvider
{
    Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken);
}

public sealed class QuotationPdfProvider(
    IQuotationRepository repository,
    IQuotationResponseComposer composer,
    IQuotationPdfRenderer renderer,
    IQuotationPdfStorage storage,
    IClock clock)
    : IQuotationPdfProvider
{
    public async Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken)
    {
        var pdf = await repository.FindPdfAsync(
            quotation.TenantId, quotation.Id, cancellationToken);

        // Generar cuesta una llamada de red a `qcode-pdf` mas una subida a R2, y la mayoria de
        // los pedidos son de cotizaciones que nadie toco desde el anterior.
        if (pdf is not null && !pdf.IsStaleFor(quotation.Version))
        {
            return pdf;
        }

        // Desde la misma respuesta que dibuja la pantalla: si el documento y la pantalla se
        // armaran por caminos distintos terminarian diciendo cosas distintas de la misma
        // cotizacion, y la diferencia la descubriria el cliente.
        var response = await composer.ComposeAsync(
            quotation.TenantId, quotation.ToDto(), cancellationToken);
        var content = await renderer.RenderAsync(
            QuotationPdfDocumentMapper.From(response), cancellationToken);
        var storageKey = await storage.SaveAsync(
            quotation.TenantId, quotation.Id, content, cancellationToken);

        var now = clock.UtcNow;
        if (pdf is null)
        {
            pdf = QuotationPdf.Generate(
                quotation.Id, quotation.TenantId, storageKey, quotation.Version, now);
            repository.AddPdf(pdf);
        }
        else
        {
            pdf.Regenerate(storageKey, quotation.Version, now);
        }

        return pdf;
    }
}
