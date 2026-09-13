using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Modules.Quotations.IntegrationTests;

internal sealed record ExportWorkbookSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<bool>> NumericCells);

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
                .ToArray()).ToArray());
    }
}
