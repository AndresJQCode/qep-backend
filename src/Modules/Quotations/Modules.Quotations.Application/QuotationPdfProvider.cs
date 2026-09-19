using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

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
    ITenantClock tenantClock,
    IQuotationLogoLookup logoLookup)
    : IQuotationPdfProvider
{
    public async Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken)
    {
        var pdf = await repository.FindPdfAsync(
            quotation.TenantId, quotation.Id, cancellationToken);

        // Una lectura de Tenancy y otra de Storage por export, sin bajar bytes todavía (spec
        // 2026-09-19, decisión 9): sólo hace falta el FileId para decidir si el PDF sigue vigente.
        var logo = await logoLookup.FindAsync(quotation.TenantId, cancellationToken);

        // Generar cuesta una llamada de red a `qcode-pdf` mas una subida a R2, y la mayoria de
        // los pedidos son de cotizaciones que nadie toco desde el anterior.
        if (pdf is not null && !pdf.IsStaleFor(quotation.Version, logo?.FileId))
        {
            return pdf;
        }

        // Desde la misma respuesta que dibuja la pantalla: si el documento y la pantalla se
        // armaran por caminos distintos terminarian diciendo cosas distintas de la misma
        // cotizacion, y la diferencia la descubriria el cliente. La fecha de emisión se imprime en
        // el día del tenant (spec 2026-09-17, punto 7).
        var calendar = await tenantClock.GetAsync(quotation.TenantId, cancellationToken);
        var response = await composer.ComposeAsync(
            quotation.TenantId, quotation.ToDto(), cancellationToken);
        // Los bytes sólo se bajan acá, al regenerar — nunca sólo para decidir si hace falta.
        var pdfLogo = logo is null
            ? null
            : new QuotationPdfLogo(
                "logo" + logo.Extension, await logoLookup.ReadAsync(logo, cancellationToken));
        var content = await renderer.RenderAsync(
            QuotationPdfDocumentMapper.From(response, calendar, pdfLogo), cancellationToken);
        var storageKey = await storage.SaveAsync(
            quotation.TenantId, quotation.Id, content, cancellationToken);

        var now = calendar.UtcNow;
        if (pdf is null)
        {
            pdf = QuotationPdf.Generate(
                quotation.Id, quotation.TenantId, storageKey, quotation.Version, logo?.FileId, now);
            repository.AddPdf(pdf);
        }
        else
        {
            pdf.Regenerate(storageKey, quotation.Version, logo?.FileId, now);
        }

        return pdf;
    }
}
