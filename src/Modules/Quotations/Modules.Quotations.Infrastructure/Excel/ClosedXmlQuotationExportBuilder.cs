using ClosedXML.Excel;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Excel;

/// <summary>
/// Arma el <c>.xlsx</c> del listado de cotizaciones con ClosedXML.
///
/// Misma forma de hoja que <c>ClosedXmlReportExcelBuilder</c> en Reporting, y por las mismas
/// razones: cabecera congelada y en negrita para que sobreviva al scroll, anchos ajustados al
/// contenido con un piso para que la cabecera no quede pegada al borde, y **la fecha como texto
/// ISO-8601** --una celda de fecha se muestra segun la configuracion regional de quien abre el
/// archivo, y ahi 03/04 deja de ser una fecha sola--.
///
/// Las columnas son las de la tabla de la pantalla, en su orden, y sin tildes como los
/// encabezados de Reporting: el archivo es "lo que estoy viendo, entero", no un reporte aparte.
/// </summary>
internal sealed class ClosedXmlQuotationExportBuilder : IQuotationExportWorkbookBuilder
{
    private const string SheetName = "Cotizaciones";

    // Mismo piso que Reporting: `AdjustToContents()` ajusta al ancho exacto del texto y deja la
    // cabecera pegada al borde de la celda siguiente.
    private const double MinimumColumnWidth = 14;

    // El ancho se mide sobre una muestra y no sobre la hoja entera. Este export no tiene tope de
    // filas --lo acota el rango de un ano--, y `AdjustToContents()` sin limites mide el texto de
    // cada celda: con un ano de un tenant grande, eso es lo que mas tarda en armar el archivo.
    // Mil filas alcanzan para que el ancho sea el de datos reales.
    private const int WidthSampleRows = 1_000;

    private static readonly IReadOnlyList<string> Columns =
    [
        "Numero",
        "Fecha",
        "Cliente",
        "Asesor",
        "Estado",
        "Moneda",
        "Total"
    ];

    public QuotationExportFile Build(
        IReadOnlyList<QuotationListItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);

        for (var column = 0; column < Columns.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = Columns[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            var excelRow = index + 2;
            sheet.Cell(excelRow, 1).Value = row.QuotationNumber;
            sheet.Cell(excelRow, 2).Value = row.CreatedAt.ToString("O");
            sheet.Cell(excelRow, 3).Value = row.ClientName ?? string.Empty;
            sheet.Cell(excelRow, 4).Value = row.AdvisorEmail ?? string.Empty;
            sheet.Cell(excelRow, 5).Value = row.Status;
            sheet.Cell(excelRow, 6).Value = row.Currency;
            // Numero y no texto: quien abre el archivo suma y filtra esta columna.
            sheet.Cell(excelRow, 7).Value = row.Total;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var sheetColumns = sheet.Columns(1, Columns.Count);
        // Fila 1 = cabecera; la muestra la incluye para que ninguna cabecera quede cortada.
        sheetColumns.AdjustToContents(1, Math.Min(rows.Count + 1, WidthSampleRows + 1));
        foreach (var column in sheetColumns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new QuotationExportFile(
            stream.ToArray(), $"cotizaciones-{generatedAt:yyyyMMdd-HHmmss}.xlsx");
    }
}
