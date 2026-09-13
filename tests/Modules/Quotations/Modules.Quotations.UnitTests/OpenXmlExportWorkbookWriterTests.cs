using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Excel;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La forma de la hoja (D8): cabecera en negrita y congelada, texto como texto, importes como
/// número, anchos fijos. Se abre el archivo con el propio SDK de OpenXML: verificar sólo que "no
/// explota" dejaría pasar una hoja con las columnas corridas.
/// </summary>
public sealed class OpenXmlExportWorkbookWriterTests
{
    private static readonly IReadOnlyList<ExportColumn> Columns =
        [new("Numero", 16), new("Fecha", 34), new("Total", 16)];

    [Fact]
    public void TheHeaderIsTheFirstRowInBoldAndFrozen()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        var sheet = Read(workbook.Complete());

        Assert.Equal("Cotizaciones", sheet.Name);
        var header = Assert.Single(sheet.Rows);
        Assert.Equal(["Numero", "Fecha", "Total"], header.Select(cell => cell.Text));
        Assert.All(header, cell => Assert.Equal(1u, cell.StyleIndex));
        Assert.True(sheet.StyleOneIsBold);
        Assert.True(sheet.HeaderIsFrozen);
    }

    [Fact]
    public void RowsKeepTheirOrderWithTextAsTextAndAmountsAsNumbers()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        workbook.AppendRow([
            ExportCell.OfText("QUO-2026-0002"),
            ExportCell.OfText("2026-09-12T15:30:00.0000000+00:00"),
            ExportCell.OfNumber(452000.50m)]);
        workbook.AppendRow([
            ExportCell.OfText("QUO-2026-0001"),
            ExportCell.OfText(null),
            ExportCell.OfNumber(0m)]);

        var sheet = Read(workbook.Complete());

        Assert.Equal(3, sheet.Rows.Count);
        var first = sheet.Rows[1];
        Assert.Equal("QUO-2026-0002", first[0].Text);
        Assert.False(first[0].IsNumber);
        // Fecha como texto ISO: una celda de fecha se mostraría según la configuración regional
        // de quien abre el archivo.
        Assert.Equal("2026-09-12T15:30:00.0000000+00:00", first[1].Text);
        Assert.False(first[1].IsNumber);
        Assert.True(first[2].IsNumber);
        Assert.Equal(452000.50m, decimal.Parse(first[2].Text, CultureInfo.InvariantCulture));
        Assert.Null(first[0].StyleIndex);
        Assert.Equal("QUO-2026-0001", sheet.Rows[2][0].Text);
        Assert.Equal(string.Empty, sheet.Rows[2][1].Text);
    }

    // Anchos fijos (D8): medir el contenido obligaría a recorrerlo dos veces.
    [Fact]
    public void ColumnsHaveTheirFixedWidths()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        var sheet = Read(workbook.Complete());

        Assert.Equal([16d, 34d, 16d], sheet.Widths);
    }

    // El temporal no queda en el disco del pod: Dispose lo borra, se haya completado o no.
    [Fact]
    public void DisposeDeletesTheTemporaryFile()
    {
        string path;
        using (var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns))
        {
            path = workbook.Complete();
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
    }

    // Una fila con más o menos celdas que columnas es un error del procesador, no un dato: correría
    // las columnas sin que nadie lo note.
    [Fact]
    public void ARowWithTheWrongNumberOfCellsIsRejected()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);

        Assert.Throws<ArgumentException>(() => workbook.AppendRow([ExportCell.OfText("solo una")]));
    }

    // El camino real de abandono: el worker falla a mitad de un export dentro de un `using` y
    // nunca llega a llamar Complete(). Dispose tiene que poder cerrar un OpenXmlWriter con
    // elementos todavía abiertos (Worksheet/SheetData sin su WriteEndElement) sin explotar, y
    // borrar igual el temporal.
    [Fact]
    public void DisposeWithoutCompleteAlsoDeletesTheTemporaryFile()
    {
        string path;
        using (var workbook = (OpenXmlExportWorkbook)new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns))
        {
            workbook.AppendRow([
                ExportCell.OfText("QUO-2026-0001"),
                ExportCell.OfText(null),
                ExportCell.OfNumber(0m)]);
            path = workbook.FilePath;
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
    }

    // Riesgo 3 del spec: en es-CO el separador decimal es la coma. Si CellValue formateara según
    // la cultura del hilo, el importe llegaría corrupto ("452000,50" no es un número válido en
    // OOXML, que exige "." per ECMA-376) y Excel lo mostraría como texto o lo rechazaría.
    [Fact]
    public void NumbersAreWrittenWithTheInvariantDecimalSeparatorRegardlessOfCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var esCo = CultureInfo.GetCultureInfo("es-CO");
        CultureInfo.CurrentCulture = esCo;
        CultureInfo.CurrentUICulture = esCo;
        try
        {
            using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
            workbook.AppendRow([
                ExportCell.OfText("QUO-2026-0001"),
                ExportCell.OfText("2026-09-12T15:30:00.0000000+00:00"),
                ExportCell.OfNumber(452000.50m)]);

            var sheet = Read(workbook.Complete());

            var rawText = sheet.Rows[1][2].Text;
            Assert.DoesNotContain(",", rawText);
            Assert.Equal(452000.50m, decimal.Parse(rawText, CultureInfo.InvariantCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // Un nombre con un caracter de control (p. ej. capturado por error de otro sistema) no puede
    // tumbar la exportación entera: sin esto, OpenXmlWriter revienta con ArgumentException al
    // escribir el texto y el job agota sus 4 intentos y falla en firme.
    [Fact]
    public void ControlCharactersThatXmlDoesNotAllowAreDroppedFromTextCells()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        workbook.AppendRow([
            ExportCell.OfText("ClienteMalo"),
            ExportCell.OfText(null),
            ExportCell.OfNumber(0m)]);

        var sheet = Read(workbook.Complete());

        Assert.Equal("ClienteMalo", sheet.Rows[1][0].Text);
    }

    private sealed record CellSnapshot(string Text, bool IsNumber, uint? StyleIndex);

    private sealed record SheetSnapshot(
        string Name,
        IReadOnlyList<IReadOnlyList<CellSnapshot>> Rows,
        IReadOnlyList<double> Widths,
        bool HeaderIsFrozen,
        bool StyleOneIsBold);

    private static SheetSnapshot Read(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;

        var rows = worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Select(row => (IReadOnlyList<CellSnapshot>)row.Elements<Cell>()
                .Select(cell => new CellSnapshot(
                    cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty,
                    cell.DataType?.Value == CellValues.Number,
                    cell.StyleIndex?.Value))
                .ToArray())
            .ToArray();
        var widths = worksheet.GetFirstChild<Columns>()!
            .Elements<Column>()
            .Select(column => column.Width!.Value)
            .ToArray();
        var pane = worksheet.GetFirstChild<SheetViews>()?.GetFirstChild<SheetView>()?.GetFirstChild<Pane>();
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var headerFontId = stylesheet.CellFormats!.Elements<CellFormat>().ElementAt(1).FontId!.Value;
        var headerFont = stylesheet.Fonts!.Elements<Font>().ElementAt((int)headerFontId);

        return new SheetSnapshot(
            sheet.Name!.Value!,
            rows,
            widths,
            pane?.State?.Value == PaneStateValues.Frozen && pane.TopLeftCell?.Value == "A2",
            headerFont.Bold is not null);
    }
}
