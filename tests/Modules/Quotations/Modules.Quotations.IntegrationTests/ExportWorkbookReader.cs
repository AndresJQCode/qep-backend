using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Modules.Quotations.IntegrationTests;

/// <param name="Formulas">La fórmula de cada celda, o null si no tiene. El enlace de un comprobante
/// de pago es una fórmula HYPERLINK (spec 2026-09-15, E3), y en <c>Rows</c> sólo se ve su valor ya
/// calculado.</param>
internal sealed record ExportWorkbookSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<bool>> NumericCells,
    IReadOnlyList<IReadOnlyList<string?>> Formulas);

/// <summary>
/// Abre el .xlsx que subió el worker con el SDK de OpenXML. Reabrir el archivo y leer celdas es la
/// convención del repo (CustomerExportApiTests): mirar sólo el status dejaría pasar un archivo con
/// las columnas corridas.
/// </summary>
internal static class ExportWorkbookReader
{
    public static ExportWorkbookSheet Read(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();

        return new ExportWorkbookSheet(
            sheet.Name!.Value!,
            rows.Select(row => (IReadOnlyList<string>)row.Elements<Cell>()
                .Select(cell => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty)
                .ToArray()).ToArray(),
            rows.Select(row => (IReadOnlyList<bool>)row.Elements<Cell>()
                .Select(cell => cell.DataType?.Value == CellValues.Number)
                .ToArray()).ToArray(),
            rows.Select(row => (IReadOnlyList<string?>)row.Elements<Cell>()
                .Select(cell => cell.CellFormula?.Text)
                .ToArray()).ToArray());
    }
}
