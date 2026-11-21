using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// Lee el Excel de empleados con ClosedXML (calco de <c>ClosedXmlServiceContractsImporter</c>). Sólo parseo estructural: lee la
/// primera hoja y ubica las 36 columnas por su encabezado, no por posición, y devuelve el texto crudo de cada celda. Las reglas
/// viven en Application.
/// </summary>
internal sealed partial class ClosedXmlEmployeeImporter : IExcelEmployeeImporter
{
    public Task<IReadOnlyList<ExcelEmployeeRow>> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un archivo corrupto, protegido o que no es un .xlsx revienta ClosedXML con excepciones de bajo nivel que no le dicen
            // nada a quien lo subió: el archivo no se puede leer, y eso es todo lo que hay que decir.
            throw new HrDomainException(HrErrorCodes.ImportUnreadable, "No pudimos leer el archivo como un libro de Excel.");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault()
                ?? throw new HrDomainException(HrErrorCodes.ImportUnreadable, "El libro de Excel no tiene hojas.");
            return Task.FromResult<IReadOnlyList<ExcelEmployeeRow>>(Read(sheet, cancellationToken));
        }
    }

    [GeneratedRegex("^0+$")]
    private static partial Regex DigitsOnlyFormat();

    // El formateo de ClosedXML redondea a 15 cifras significativas; un entero exacto (hasta 2^53) se rellena a mano para no alterar una cuenta.
    private static string PaddedDigits(IXLCell cell)
    {
        var value = cell.GetDouble();
        return value is >= 0 and < 9_007_199_254_740_992 && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture).PadLeft(cell.Style.NumberFormat.Format.Length, '0')
            : cell.GetFormattedString();
    }

    private static List<ExcelEmployeeRow> Read(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columnByName = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in HrImportColumns.Ordered)
        {
            if (!columnByName.TryGetValue(HrText.Fold(expected)!, out var index))
            {
                throw new HrDomainException(
                    HrErrorCodes.ImportHeaderMismatch,
                    "Las columnas no coinciden con la plantilla. Descarga la plantilla de nuevo.");
            }

            indexes[expected] = index;
        }

        var rows = new List<ExcelEmployeeRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = worksheet.Row(rowNumber);
            var cells = HrImportColumns.Ordered.ToDictionary(column => column, column => Cell(row, indexes[column]), StringComparer.Ordinal);

            // Una fila en blanco entre los datos es ruido de formato, no una fila que reportar.
            if (cells.Values.All(cell => cell is null))
            {
                continue;
            }

            rows.Add(new ExcelEmployeeRow(rowNumber, cells));
        }

        return rows;
    }

    // Sin mayúsculas ni tildes: «Telefono» retipeado a mano es «Teléfono».
    private static Dictionary<string, int> MapHeaderColumns(IXLWorksheet worksheet)
    {
        var header = worksheet.Row(1);
        var lastColumn = header.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var columnByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= lastColumn; column++)
        {
            if (HrText.Fold(header.Cell(column).GetString()) is { } name)
            {
                columnByName.TryAdd(name, column);
            }
        }

        return columnByName;
    }

    /// <summary>
    /// Una fecha o un número que Excel guarda como tales salen en texto invariante (<c>yyyy-MM-dd</c>, punto decimal): el texto
    /// formateado depende de la configuración regional de quien hizo el archivo.
    /// </summary>
    private static string? Cell(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var raw = cell.DataType switch
        {
            XLDataType.DateTime => DateOnly.FromDateTime(cell.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // Un formato de sólo ceros («00001234», «0») es el texto que la persona ve: conserva los ceros a la izquierda. El resto se
            // lee como número invariante, porque un formato de moneda o fecha regional no lo entiende el parser.
            XLDataType.Number when DigitsOnlyFormat().IsMatch(cell.Style.NumberFormat.Format) => PaddedDigits(cell),
            XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
