using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Excel;

/// <summary>
/// El Excel de las exportaciones asíncronas, en streaming a disco (D8). ClosedXML guarda cada
/// celda como objeto hasta el final; con un año de un tenant grande eso son cientos de MB en el pod
/// de 1Gi que comparte con la API. Acá el paquete es un zip propio en modo Create: cada fila sale
/// comprimida al temporal apenas llega, y la memoria queda acotada al lote que el procesador tiene
/// en la mano.
///
/// No se usa <c>SpreadsheetDocument.Create</c>: abre el zip en modo update, que guarda cada parte
/// sin comprimir en memoria hasta el Dispose —se midió cerca de 1 KB por fila, con el temporal en
/// 0 bytes hasta el final—, y el SDK no tiene un camino de sólo escritura (rechaza un stream que no
/// se puede leer).
/// </summary>
internal sealed class OpenXmlExportWorkbookWriter : IExportWorkbookWriter
{
    public IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns) =>
        OpenXmlExportWorkbook.Start(sheetName, columns);
}

internal sealed class OpenXmlExportWorkbook : IExportWorkbook
{
    // Índice 1 de CellFormats: la fuente en negrita de BuildStylesheet.
    private const uint HeaderStyleIndex = 1;

    // Índice 2 de CellFormats: la fuente azul y subrayada de un enlace (spec 2026-09-15, E3).
    private const uint LinkStyleIndex = 2;

    // El color de los enlaces de Office, en ARGB.
    private const string LinkColor = "FF0563C1";

    // El tope de Excel para una cadena dentro de una fórmula (E4).
    private const int MaxFormulaStringLength = 255;

    // El id con que workbook.xml apunta a la hoja; lo resuelve xl/_rels/workbook.xml.rels.
    private const string SheetRelationshipId = "rId1";

    private const string ContentTypesXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
        """;

    private const string PackageRelationshipsXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
        """;

    private const string WorkbookRelationshipsXml =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
        """;

    private readonly string _path;
    private readonly ZipArchive _archive;
    private readonly Stream _sheetStream;
    private readonly OpenXmlWriter _writer;
    private readonly int _columnCount;
    private uint _nextRowIndex = 1;
    private bool _closed;

    // Sólo para pruebas (InternalsVisibleTo a Modules.Quotations.UnitTests): el contrato público
    // IExportWorkbook no expone la ruta antes de Complete(), y no hace falta que la exponga —el
    // camino de abandono (Dispose sin Complete) sólo necesita verificarse desde el test.
    internal string FilePath => _path;

    private OpenXmlExportWorkbook(
        string path, ZipArchive archive, Stream sheetStream, OpenXmlWriter writer, int columnCount)
    {
        _path = path;
        _archive = archive;
        _sheetStream = sheetStream;
        _writer = writer;
        _columnCount = columnCount;
    }

    public static OpenXmlExportWorkbook Start(string sheetName, IReadOnlyList<ExportColumn> columns)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qep-export-{Guid.NewGuid():N}.xlsx");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // Declarados afuera del try: si algo revienta a mitad, el catch los cierra en el mismo
        // orden que Complete() —writer, entrada, zip y archivo— antes de borrar el temporal.
        ZipArchive? archive = null;
        Stream? sheetStream = null;
        OpenXmlWriter? writer = null;
        try
        {
            // En modo Create cada entrada va directo al archivo mientras se escribe, pero sólo
            // puede haber una abierta a la vez: las partes chicas van primero y completas, y la
            // hoja queda última y abierta hasta Complete().
            archive = new ZipArchive(file, ZipArchiveMode.Create);
            WriteText(archive, "[Content_Types].xml", ContentTypesXml);
            WriteText(archive, "_rels/.rels", PackageRelationshipsXml);
            // Workbook y estilos van por DOM —son chicos— y se serializan con el propio SDK: así
            // el nombre de la hoja sale escapado como cualquier atributo XML.
            WritePart(archive, "xl/workbook.xml", new Workbook(new Sheets(new Sheet
            {
                Id = SheetRelationshipId,
                SheetId = 1U,
                Name = sheetName,
            })));
            WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml);
            WritePart(archive, "xl/styles.xml", BuildStylesheet());

            sheetStream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
            writer = OpenXmlWriter.Create(sheetStream);
            writer.WriteStartElement(new Worksheet());
            writer.WriteElement(FrozenHeaderView());
            writer.WriteElement(new Columns(columns.Select((column, index) => new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = column.Width,
                CustomWidth = true,
            })));
            writer.WriteStartElement(new SheetData());

            var workbook = new OpenXmlExportWorkbook(path, archive, sheetStream, writer, columns.Count);
            workbook.WriteRow(columns.Select(column => ExportCell.OfText(column.Header)).ToArray(), HeaderStyleIndex);
            return workbook;
        }
        catch
        {
            // La limpieza no puede tapar la excepción original: cada paso va en su propio
            // try/catch para que uno que falle no impida los siguientes, y el catch de acá
            // nunca atrapa nada del bloque de arriba, así que el `throw;` de abajo siempre
            // relanza la causa real y no un IOException del archivo bloqueado.
            try
            {
                writer?.Dispose();
            }
            catch
            {
                // Deliberado: ver el comentario de arriba.
            }

            try
            {
                sheetStream?.Dispose();
            }
            catch
            {
                // Deliberado: ver el comentario de arriba.
            }

            try
            {
                archive?.Dispose();
            }
            catch
            {
                // Deliberado: ver el comentario de arriba.
            }

            try
            {
                // Si el zip no llegó a crearse, nadie cerró el archivo; si sí, esto no hace nada.
                file.Dispose();
            }
            catch
            {
                // Deliberado: ver el comentario de arriba.
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Deliberado: ver el comentario de arriba.
            }

            throw;
        }
    }

    public void AppendRow(IReadOnlyList<ExportCell> cells)
    {
        if (cells.Count != _columnCount)
        {
            throw new ArgumentException(
                $"The row has {cells.Count} cells but the sheet has {_columnCount} columns.", nameof(cells));
        }

        WriteRow(cells, styleIndex: null);
    }

    public string Complete()
    {
        if (!_closed)
        {
            _writer.WriteEndElement(); // SheetData
            _writer.WriteEndElement(); // Worksheet
            _writer.Close();
            _sheetStream.Dispose();
            // Escribe el directorio central del zip y cierra el archivo.
            _archive.Dispose();
            _closed = true;
        }

        return _path;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _writer.Dispose();
            _sheetStream.Dispose();
            _archive.Dispose();
            _closed = true;
        }

        // File.Delete no falla si el archivo no está.
        File.Delete(_path);
    }

    private static void WriteText(ZipArchive archive, string entryName, string xml)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        // Sin BOM: la declaración ya dice UTF-8.
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(xml);
    }

    private static void WritePart(ZipArchive archive, string entryName, OpenXmlPartRootElement root)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        root.Save(stream);
    }

    private void WriteRow(IReadOnlyList<ExportCell> cells, uint? styleIndex)
    {
        var rowIndex = _nextRowIndex++;
        var rowNumber = rowIndex.ToString(CultureInfo.InvariantCulture);
        _writer.WriteStartElement(new Row { RowIndex = rowIndex });
        for (var index = 0; index < cells.Count; index++)
        {
            _writer.WriteElement(ToCell(cells[index], ColumnName(index) + rowNumber, styleIndex));
        }

        _writer.WriteEndElement();
    }

    // Texto inline y no la tabla de strings compartidos: la tabla se arma en memoria hasta el
    // final, que es justo lo que el streaming evita. Por lo mismo el enlace es una fórmula y no un
    // hipervínculo de relación (spec 2026-09-15, E3): ése vive en <hyperlinks>, después de
    // <sheetData>, y en sheet1.xml.rels, y el zip admite una sola entrada abierta a la vez, así que
    // habría que guardar todos los enlaces en memoria hasta el final.
    private static Cell ToCell(ExportCell value, string reference, uint? styleIndex)
    {
        Cell cell;
        var style = styleIndex;
        if (value.Number is { } number)
        {
            cell = new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) };
        }
        else if (value.Url is { } url && HyperlinkFormula(url, value.Text ?? string.Empty) is { } formula)
        {
            // <v> lleva el valor ya calculado: el archivo se ve bien antes de que Excel recalcule, y
            // en visores que no calculan.
            cell = new Cell
            {
                DataType = CellValues.String,
                CellFormula = new CellFormula(formula),
                CellValue = new CellValue(RemoveInvalidXmlChars(value.Text ?? string.Empty)),
            };
            style ??= LinkStyleIndex;
        }
        else
        {
            // E4: una URL que no entra en la fórmula sale como texto plano, para que se vea en vez de
            // perderse.
            cell = TextCell(value.Url ?? value.Text);
        }

        cell.CellReference = reference;
        if (style is { } appliedStyle)
        {
            cell.StyleIndex = appliedStyle;
        }

        return cell;
    }

    private static Cell TextCell(string? text) =>
        new()
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(RemoveInvalidXmlChars(text ?? string.Empty))
            {
                Space = SpaceProcessingModeValues.Preserve,
            }),
        };

    // HYPERLINK("url","texto"). En el XML los argumentos van separados con coma sin importar la
    // configuración regional: Excel la muestra con el separador de quien abre el archivo. Las
    // comillas dobles se escapan duplicándolas. Null si alguna de las dos cadenas pasa el tope de
    // Excel (E4); se mide ya escapada, que es lo que Excel lee dentro de la fórmula.
    private static string? HyperlinkFormula(string url, string text)
    {
        var escapedUrl = EscapeFormulaString(RemoveInvalidXmlChars(url));
        var escapedText = EscapeFormulaString(RemoveInvalidXmlChars(text));
        return escapedUrl.Length > MaxFormulaStringLength || escapedText.Length > MaxFormulaStringLength
            ? null
            : $"HYPERLINK(\"{escapedUrl}\",\"{escapedText}\")";
    }

    private static string EscapeFormulaString(string value) =>
        value.Replace("\"", "\"\"", StringComparison.Ordinal);

    // Un dato de otro sistema puede traer un caracter de control que XML no admite (p. ej.
    // U+0001 colado en un nombre): OpenXmlWriter usa XmlWriter por debajo, que revienta con
    // ArgumentException al escribirlo, y eso agotaría los 4 intentos del job por una sola fila
    // sucia. Se descarta el caracter y se sigue: el archivo es más importante que ese byte.
    private static string RemoveInvalidXmlChars(string text)
    {
        var firstInvalid = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (!XmlConvert.IsXmlChar(text[index]))
            {
                firstInvalid = index;
                break;
            }
        }

        // Camino rápido: el caso normal no tiene nada que sacar, y no hay que reservar memoria
        // para copiarlo tal cual.
        if (firstInvalid < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstInvalid);
        for (var index = firstInvalid; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsSurrogatePair(text, index) && XmlConvert.IsXmlSurrogatePair(text[index + 1], current))
            {
                builder.Append(current);
                builder.Append(text[index + 1]);
                index++;
            }
            else if (XmlConvert.IsXmlChar(current))
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    // 0 → A, 25 → Z, 26 → AA.
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var remaining = index + 1; remaining > 0; remaining = (remaining - 1) / 26)
        {
            name = (char)('A' + ((remaining - 1) % 26)) + name;
        }

        return name;
    }

    // La cabecera queda fija al hacer scroll, igual que en los Excel de Reporting.
    private static SheetViews FrozenHeaderView() =>
        new(new SheetView(
            new Pane
            {
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            },
            new Selection { Pane = PaneValues.BottomLeft })
        {
            WorkbookViewId = 0U,
        });

    // Lo mínimo que Excel acepta sin quejarse: tres fuentes (normal, negrita y la azul subrayada de
    // los enlaces), los dos rellenos que la especificación exige, un borde vacío y tres formatos de
    // celda (normal, cabecera y enlace).
    private static Stylesheet BuildStylesheet() =>
        new(
            new Fonts(
                new Font(),
                new Font(new Bold()),
                new Font(new Underline(), new Color { Rgb = HexBinaryValue.FromString(LinkColor) }))
            {
                Count = 3U,
            },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
            {
                Count = 2U,
            },
            new Borders(new Border(
                new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()))
            {
                Count = 1U,
            },
            new CellFormats(
                new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U },
                new CellFormat { FontId = 1U, FillId = 0U, BorderId = 0U, ApplyFont = true },
                new CellFormat { FontId = 2U, FillId = 0U, BorderId = 0U, ApplyFont = true })
            {
                Count = 3U,
            });
}
