using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ExportQuotationPdfCommand(
    Guid TenantId, Guid QuotationId) : ICommand<QuotationPdfExportDto>;

/// <summary>El enlace de descarga y cuándo se generó el documento que hay detrás. La URL es
/// firmada y de vida corta: la descarga va directo de R2 al navegador.</summary>
public sealed record QuotationPdfExportDto(string Url, DateTimeOffset GeneratedAt);

public sealed class ExportQuotationPdfHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationResponseComposer composer,
    IQuotationPdfRenderer renderer,
    IQuotationPdfStorage storage,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportQuotationPdfCommand, QuotationPdfExportDto>
{
    public async Task<QuotationPdfExportDto> HandleAsync(
        ExportQuotationPdfCommand command, CancellationToken cancellationToken)
    {
        // Exportar es leer la cotización en otro formato: quien la ve en pantalla ya ve sus
        // precios. Mismo criterio que el export de clientes, que pide el permiso del listado.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationRead);

        var quotationId = new QuotationId(command.QuotationId);
        var quotation = await repository.FindAsync(command.TenantId, quotationId, cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var pdf = await repository.FindPdfAsync(command.TenantId, quotationId, cancellationToken);

        // El unico trabajo real de este caso de uso. Generar cuesta una llamada de red a
        // `qcode-pdf` mas una subida a R2, y la mayoria de las exportaciones son de cotizaciones
        // que nadie toco desde la anterior: sin esta comparacion, abrir dos veces la misma
        // pantalla pagaria las dos.
        if (pdf is null || pdf.IsStaleFor(quotation.Version))
        {
            var response = await composer.ComposeAsync(
                command.TenantId, quotation.ToDto(), cancellationToken);
            var content = await renderer.RenderAsync(
                QuotationPdfDocumentMapper.From(response), cancellationToken);
            var storageKey = await storage.SaveAsync(
                command.TenantId, quotationId, content, cancellationToken);

            var now = clock.UtcNow;
            if (pdf is null)
            {
                pdf = QuotationPdf.Generate(
                    quotationId, command.TenantId, storageKey, quotation.Version, now);
                repository.AddPdf(pdf);
            }
            else
            {
                pdf.Regenerate(storageKey, quotation.Version, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var url = await storage.CreateDownloadUrlAsync(
            pdf.StorageKey,
            $"Cotizacion-{quotation.QuotationNumber}.pdf",
            cancellationToken);

        return new QuotationPdfExportDto(url, pdf.GeneratedAt);
    }
}
