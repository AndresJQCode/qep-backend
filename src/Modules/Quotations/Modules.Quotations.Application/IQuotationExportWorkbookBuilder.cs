namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de cotizaciones. Puerto y no una clase concreta por el mismo motivo
/// que <c>IProductExportWorkbookBuilder</c> en Catalog: ClosedXML es una decision de
/// infraestructura y la capa de aplicacion no deberia compilar contra ella.
/// </summary>
public interface IQuotationExportWorkbookBuilder
{
    QuotationExportFile Build(
        IReadOnlyList<QuotationListItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
}

/// <summary>El archivo listo para bajar: los bytes y el nombre con que el navegador lo guarda.
/// Misma forma que <c>ReportFile</c> en Reporting.</summary>
public sealed record QuotationExportFile(byte[] Content, string FileName);
