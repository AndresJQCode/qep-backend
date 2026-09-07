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
    IQuotationPdfProvider pdfProvider,
    IQuotationPdfStorage storage,
    IExecutionContext executionContext)
    : ICommandHandler<ExportQuotationPdfCommand, QuotationPdfExportDto>
{
    public async Task<QuotationPdfExportDto> HandleAsync(
        ExportQuotationPdfCommand command, CancellationToken cancellationToken)
    {
        // Exportar es leer la cotización en otro formato: quien la ve en pantalla ya ve sus
        // precios. Mismo criterio que el export de clientes, que pide el permiso del listado.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationRead);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var pdf = await pdfProvider.EnsureCurrentAsync(quotation, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var url = await storage.CreateDownloadUrlAsync(
            pdf.StorageKey,
            $"Cotizacion-{quotation.QuotationNumber}.pdf",
            cancellationToken);

        return new QuotationPdfExportDto(url, pdf.GeneratedAt);
    }
}
