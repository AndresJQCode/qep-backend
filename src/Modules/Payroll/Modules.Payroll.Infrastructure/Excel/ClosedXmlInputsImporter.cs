using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Excel;

/// <summary>
/// Lee el Excel de novedades con ClosedXML (calco de <c>ClosedXmlEmployeeImporter</c> de Hr). Sólo parseo estructural: lee la hoja
/// <c>Novedades</c> (o la primera si el libro no la tiene) y ubica las columnas por su encabezado, no por posición; devuelve el texto
/// crudo de cada celda. Las reglas viven en Application.
/// </summary>
internal sealed partial class ClosedXmlInputsImporter : IExcelInputsImporter
{
    public Task<IReadOnlyList<ExcelInputsRow>> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A corrupt, protected or non-xlsx file blows up ClosedXML with low-level errors that tell the user nothing.
            throw new PayrollDomainException(PayrollErrorCodes.ImportUnreadable, "No pudimos leer el archivo como un libro de Excel.");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(candidate =>
                    PayrollText.Fold(candidate.Name) == PayrollText.Fold(InputsImportColumns.DataSheetName))
                ?? workbook.Worksheets.FirstOrDefault()
                ?? throw new PayrollDomainException(PayrollErrorCodes.ImportUnreadable, "El libro de Excel no tiene hojas.");
            return Task.FromResult<IReadOnlyList<ExcelInputsRow>>(Read(sheet, cancellationToken));
        }
    }

    [GeneratedRegex("^0+$")]
    private static partial Regex DigitsOnlyFormat();

    // ClosedXML formats to 15 significant digits; an exact integer is padded by hand so a document is not altered.
    private static string PaddedDigits(IXLCell cell)
    {
        var value = cell.GetDouble();
        return value is >= 0 and < 9_007_199_254_740_992 && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture).PadLeft(cell.Style.NumberFormat.Format.Length, '0')
            : cell.GetFormattedString();
    }

    private static List<ExcelInputsRow> Read(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columnByName = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in InputsImportColumns.Read)
        {
            if (!columnByName.TryGetValue(PayrollText.Fold(expected)!, out var index))
            {
                throw new PayrollDomainException(
                    PayrollErrorCodes.ImportHeaderMismatch,
                    "Las columnas no coinciden con la plantilla. Descarga la plantilla de nuevo.");
            }

            indexes[expected] = index;
        }

        var rows = new List<ExcelInputsRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = worksheet.Row(rowNumber);
            var cells = InputsImportColumns.Read.ToDictionary(column => column, column => Cell(row, indexes[column]), StringComparer.Ordinal);

            // A blank row between the data is formatting noise, not a row to report.
            if (cells.Values.All(cell => cell is null))
            {
                continue;
            }

            rows.Add(new ExcelInputsRow(rowNumber, cells));

            // MaxRows + 1 rows are enough for the analyzer to answer too_many_rows; walking on to the last used row (up to a million)
            // would read every cell of a hostile workbook for nothing.
            if (rows.Count > InputsImportColumns.MaxRows)
            {
                break;
            }
        }

        return rows;
    }

    private static Dictionary<string, int> MapHeaderColumns(IXLWorksheet worksheet)
    {
        var header = worksheet.Row(1);
        var lastColumn = header.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var columnByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= lastColumn; column++)
        {
            if (PayrollText.Fold(header.Cell(column).GetString()) is { } name)
            {
                columnByName.TryAdd(name, column);
            }
        }

        return columnByName;
    }

    private static string? Cell(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var raw = cell.DataType switch
        {
            XLDataType.Number when DigitsOnlyFormat().IsMatch(cell.Style.NumberFormat.Format) => PaddedDigits(cell),
            XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
