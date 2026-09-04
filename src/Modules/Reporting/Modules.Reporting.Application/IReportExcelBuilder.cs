namespace Modules.Reporting.Application;

/// <summary>
/// Arma el <c>.xlsx</c> de cada reporte.
///
/// Un solo puerto con cuatro metodos y no cuatro puertos: la implementacion es una sola clase
/// con una sola forma de hoja (cabecera congelada en negrita, anchos ajustados, fechas como
/// texto ISO-8601), y partirla en cuatro registros de contenedor no compraria nada.
///
/// En Infrastructure y no en el composition root, a diferencia de los origenes de datos: armar
/// un Excel no cruza ninguna frontera de modulo, solo necesita ClosedXML.
/// </summary>
public interface IReportExcelBuilder
{
    ReportFile BuildSales(
        IReadOnlyList<SalesReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    ReportFile BuildQuotations(
        IReadOnlyList<QuotationsReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    ReportFile BuildPriceChanges(
        IReadOnlyList<PriceChangeReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    ReportFile BuildCustomers(
        IReadOnlyList<CustomerReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
}
