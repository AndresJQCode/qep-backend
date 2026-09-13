using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Excel;

/// <summary>
/// El Excel de las exportaciones asíncronas, en streaming (D8). ClosedXML guarda cada celda como
/// objeto hasta el final; con un año de un tenant grande eso son cientos de MB en el pod de 1Gi
/// que comparte con la API. Acá cada fila se escribe al temporal apenas llega y la memoria queda
/// acotada al lote que el procesador tiene en la mano.
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

    private readonly string _path;
    private readonly SpreadsheetDocument _document;
    private readonly OpenXmlWriter _writer;
    private readonly int _columnCount;
    private uint _nextRowIndex = 1;
    private bool _closed;

    // Sólo para pruebas (InternalsVisibleTo a Modules.Quotations.UnitTests): el contrato público
    // IExportWorkbook no expone la ruta antes de Complete(), y no hace falta que la exponga —el
    // camino de abandono (Dispose sin Complete) sólo necesita verificarse desde el test.
    internal string FilePath => _path;

    private OpenXmlExportWorkbook(
        string path, SpreadsheetDocument document, OpenXmlWriter writer, int columnCount)
    {
        _path = path;
        _document = document;
        _writer = writer;
        _columnCount = columnCount;
    }

    public static OpenXmlExportWorkbook Start(string sheetName, IReadOnlyList<ExportColumn> columns)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qep-export-{Guid.NewGuid():N}.xlsx");
        var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        // Declarado afuera del try: si algo revienta después de crearlo, el catch necesita
        // cerrarlo antes que el documento (mismo orden que Complete()), porque disponer el
        // documento con el part writer todavía abierto es inseguro.
        OpenXmlWriter? writer = null;
        try
        {
            var workbookPart = document.AddWorkbookPart();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = BuildStylesheet();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            workbookPart.Workbook = new Workbook(new Sheets(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1U,
                Name = sheetName,
            }));

            // La hoja se escribe con OpenXmlWriter y nunca se toca worksheetPart.Worksheet: el
            // autoguardado del documento reescribiría la parte con un DOM vacío. Workbook y estilos
            // sí van por DOM —son chicos— y se guardan solos al cerrar el documento.
            writer = OpenXmlWriter.Create(worksheetPart);
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

            var workbook = new OpenXmlExportWorkbook(path, document, writer, columns.Count);
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
                document.Dispose();
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
            _document.Dispose();
            _closed = true;
        }

        return _path;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _writer.Dispose();
            _document.Dispose();
            _closed = true;
        }

        // File.Delete no falla si el archivo no está.
        File.Delete(_path);
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
    // final, que es justo lo que el streaming evita.
    private static Cell ToCell(ExportCell value, string reference, uint? styleIndex)
    {
        var cell = value.Number is { } number
            ? new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) }
            : new Cell
            {
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(RemoveInvalidXmlChars(value.Text ?? string.Empty))
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            };
        cell.CellReference = reference;
        if (styleIndex is { } style)
        {
            cell.StyleIndex = style;
        }

        return cell;
    }

    // Un dato de otro sistema puede traer un caracter de control que XML no admite (p. ej.
    // "" colado en un nombre): OpenXmlWriter usa XmlWriter por debajo, que revienta con
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

    // Lo mínimo que Excel acepta sin quejarse: dos fuentes (normal y negrita), los dos rellenos
    // que la especificación exige, un borde vacío y dos formatos de celda.
    private static Stylesheet BuildStylesheet() =>
        new(
            new Fonts(new Font(), new Font(new Bold())) { Count = 2U },
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
                new CellFormat { FontId = 1U, FillId = 0U, BorderId = 0U, ApplyFont = true })
            {
                Count = 2U,
            });
}
