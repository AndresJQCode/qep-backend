using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
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

    // D8: la memoria queda acotada al lote porque cada fila llega al temporal apenas se escribe.
    // Si el paquete se armara en memoria hasta el Dispose —lo que hacía SpreadsheetDocument.Create,
    // con el zip en modo update—, el archivo seguiría en 0 bytes después de miles de filas.
    [Fact]
    public void RowsReachTheTemporaryFileBeforeTheWorkbookIsCompleted()
    {
        using var workbook = (OpenXmlExportWorkbook)new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        for (var index = 0; index < 10_000; index++)
        {
            workbook.AppendRow([
                ExportCell.OfText($"QUO-2026-{index:D5}"),
                ExportCell.OfText("2026-09-12T15:30:00.0000000+00:00"),
                ExportCell.OfNumber(452000.50m)]);
        }

        Assert.True(new FileInfo(workbook.FilePath).Length > 0);
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
            ExportCell.OfText("Cliente\u0001Malo"),
            ExportCell.OfText(null),
            ExportCell.OfNumber(0m)]);

        var sheet = Read(workbook.Complete());

        Assert.Equal("ClienteMalo", sheet.Rows[1][0].Text);
    }

    // Un emoji es un par de surrogates válido y tiene que llegar entero; un surrogate suelto no es
    // XML válido y se descarta como cualquier otro caracter prohibido. El primer caso fija también
    // el orden de los argumentos de XmlConvert.IsXmlSurrogatePair (bajo, alto), fácil de invertir.
    //
    // MemberData sin enumerar en el descubrimiento, y no InlineData: xUnit serializa en UTF-8 los
    // argumentos que descubre, y un surrogate suelto llega a la prueba convertido en U+FFFD.
    public static TheoryData<string, string> SurrogateCases => new()
    {
        { "A\U0001F600B", "A\U0001F600B" },
        { "A\uD800B", "AB" },
    };

    [Theory]
    [MemberData(nameof(SurrogateCases), DisableDiscoveryEnumeration = true)]
    public void SurrogatePairsSurviveAndLoneSurrogatesAreDropped(string text, string expected)
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        workbook.AppendRow([
            ExportCell.OfText(text),
            ExportCell.OfText(null),
            ExportCell.OfNumber(0m)]);

        var sheet = Read(workbook.Complete());

        Assert.Equal(expected, sheet.Rows[1][0].Text);
    }

    // El paquete lo arma este writer a mano (content types, relaciones, workbook, estilos y hoja),
    // así que además de leerlo con el SDK se valida contra el esquema: un error acá es un archivo
    // que Excel puede abrir con "reparar" o no abrir.
    [Fact]
    public void TheCompletedFilePassesTheOpenXmlValidator()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Cotizaciones", Columns);
        workbook.AppendRow([
            ExportCell.OfText("QUO-2026-0001"),
            ExportCell.OfText("2026-09-12T15:30:00.0000000+00:00"),
            ExportCell.OfNumber(452000.50m)]);

        using var document = SpreadsheetDocument.Open(workbook.Complete(), isEditable: false);
        var errors = new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken)
            .Select(error => $"{error.ErrorType} {error.Part?.Uri} {error.Path?.XPath}: {error.Description}")
            .ToArray();

        Assert.Empty(errors);
    }

    // Spec 2026-09-15, E3: el enlace es la fórmula HYPERLINK con el valor ya calculado —se ve bien
    // antes de que Excel recalcule, y en visores que no calculan— y el estilo de enlace.
    [Fact]
    public void ALinkCellIsAHyperlinkFormulaWithItsCachedValueAndTheLinkStyle()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/payment-proofs/abc.pdf", "Ver"),
            ExportCell.OfNumber(1m)]);

        var sheet = Read(workbook.Complete());

        var link = sheet.Rows[1][1];
        Assert.Equal("HYPERLINK(\"https://assets.qep.test/payment-proofs/abc.pdf\",\"Ver\")", link.Formula);
        Assert.Equal("Ver", link.Text);
        Assert.Equal(CellValues.String, link.Type);
        Assert.Equal(2u, link.StyleIndex);
        Assert.True(sheet.StyleTwoIsALink);
        Assert.Null(sheet.Rows[1][0].Formula);
        Assert.Null(sheet.Rows[1][0].StyleIndex);
    }

    // E3: las comillas dobles se escapan duplicándolas, en la URL y en el texto; el valor calculado
    // queda sin escapar.
    [Fact]
    public void QuotesInTheUrlAndTheTextAreDoubled()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/a\"b.pdf", "Ver \"1\""),
            ExportCell.OfNumber(1m)]);

        var link = Read(workbook.Complete()).Rows[1][1];

        Assert.Equal("HYPERLINK(\"https://assets.qep.test/a\"\"b.pdf\",\"Ver \"\"1\"\"\")", link.Formula);
        Assert.Equal("Ver \"1\"", link.Text);
    }

    // E4: 255 es el tope de Excel para una cadena dentro de una fórmula. Hasta ahí, enlace.
    [Fact]
    public void AUrlOf255CharactersIsStillALink()
    {
        var url = UrlOfLength(255);
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([ExportCell.OfText("PED-2026-0001"), ExportCell.OfLink(url, "Ver"), ExportCell.OfNumber(1m)]);

        var link = Read(workbook.Complete()).Rows[1][1];

        Assert.Equal($"HYPERLINK(\"{url}\",\"Ver\")", link.Formula);
        Assert.Equal("Ver", link.Text);
    }

    // E4: una más y la celda lleva la URL como texto plano, sin estilo de enlace, para que se vea en
    // vez de perderse.
    [Fact]
    public void AUrlLongerThan255CharactersIsWrittenAsPlainText()
    {
        var url = UrlOfLength(256);
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([ExportCell.OfText("PED-2026-0001"), ExportCell.OfLink(url, "Ver"), ExportCell.OfNumber(1m)]);

        var cell = Read(workbook.Complete()).Rows[1][1];

        Assert.Null(cell.Formula);
        Assert.Equal(url, cell.Text);
        Assert.Equal(CellValues.InlineString, cell.Type);
        Assert.Null(cell.StyleIndex);
    }

    // Una celda de enlace y el tercer formato de celda no pueden dejar el archivo inválido.
    [Fact]
    public void AFileWithALinkPassesTheOpenXmlValidator()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/payment-proofs/abc.pdf", "Ver"),
            ExportCell.OfNumber(1m)]);

        using var document = SpreadsheetDocument.Open(workbook.Complete(), isEditable: false);
        var errors = new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken)
            .Select(error => $"{error.ErrorType} {error.Part?.Uri} {error.Path?.XPath}: {error.Description}")
            .ToArray();

        Assert.Empty(errors);
    }

    private static string UrlOfLength(int length)
    {
        const string Prefix = "https://assets.qep.test/payment-proofs/";
        return Prefix + new string('a', length - Prefix.Length);
    }

    private sealed record CellSnapshot(
        string Text, bool IsNumber, uint? StyleIndex, string? Formula, CellValues? Type);

    private sealed record SheetSnapshot(
        string Name,
        IReadOnlyList<IReadOnlyList<CellSnapshot>> Rows,
        IReadOnlyList<double> Widths,
        bool HeaderIsFrozen,
        bool StyleOneIsBold,
        bool StyleTwoIsALink);

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
                    cell.StyleIndex?.Value,
                    cell.CellFormula?.Text,
                    cell.DataType?.Value))
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
        // El formato 2 es el del enlace (E3): fuente subrayada y azul de Office.
        var linkFontId = stylesheet.CellFormats!.Elements<CellFormat>().ElementAtOrDefault(2)?.FontId?.Value;
        var linkFont = linkFontId is { } fontId
            ? stylesheet.Fonts!.Elements<Font>().ElementAtOrDefault((int)fontId)
            : null;

        return new SheetSnapshot(
            sheet.Name!.Value!,
            rows,
            widths,
            pane?.State?.Value == PaneStateValues.Frozen && pane.TopLeftCell?.Value == "A2",
            headerFont.Bold is not null,
            linkFont?.Underline is not null
                && string.Equals(linkFont.Color?.Rgb?.Value, "FF0563C1", StringComparison.OrdinalIgnoreCase));
    }
}
