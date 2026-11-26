using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// Reads the exam history Excel with ClosedXML (same approach as <c>ClosedXmlEmployeeImporter</c>). Only structural parsing: it reads
/// the first sheet, finds the eight columns by header (not by position) and returns the raw text of each cell. The rules live in
/// Application.
/// </summary>
internal sealed partial class ClosedXmlMedicalExamImporter : IExcelMedicalExamImporter
{
    public Task<IReadOnlyList<ExcelMedicalExamRow>> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A corrupt, protected or non-xlsx file blows ClosedXML up with low level exceptions that tell nothing to whoever
            // uploaded it: the file cannot be read, and that is all there is to say.
            throw new HrDomainException(HrErrorCodes.ImportUnreadable, "No pudimos leer el archivo como un libro de Excel.");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault()
                ?? throw new HrDomainException(HrErrorCodes.ImportUnreadable, "El libro de Excel no tiene hojas.");
            return Task.FromResult<IReadOnlyList<ExcelMedicalExamRow>>(Read(sheet, cancellationToken));
        }
    }

    [GeneratedRegex("^0+$")]
    private static partial Regex DigitsOnlyFormat();

    // ClosedXML rounds to 15 significant digits; an exact integer (up to 2^53) is padded by hand so a document number is never altered.
    private static string PaddedDigits(IXLCell cell)
    {
        var value = cell.GetDouble();
        return value is >= 0 and < 9_007_199_254_740_992 && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture).PadLeft(cell.Style.NumberFormat.Format.Length, '0')
            : cell.GetFormattedString();
    }

    private static List<ExcelMedicalExamRow> Read(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columnByName = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in MedicalExamImportColumns.Ordered)
        {
            if (!columnByName.TryGetValue(HrText.Fold(expected)!, out var index))
            {
                throw new HrDomainException(
                    HrErrorCodes.ImportHeaderMismatch,
                    "Las columnas no coinciden con la plantilla. Descarga la plantilla de nuevo.");
            }

            indexes[expected] = index;
        }

        var rows = new List<ExcelMedicalExamRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = worksheet.Row(rowNumber);
            var cells = MedicalExamImportColumns.Ordered.ToDictionary(column => column, column => Cell(row, indexes[column]), StringComparer.Ordinal);

            // A blank row between the data is formatting noise, not a row to report.
            if (cells.Values.All(cell => cell is null))
            {
                continue;
            }

            rows.Add(new ExcelMedicalExamRow(rowNumber, cells));
        }

        return rows;
    }

    // Without case or accents: a header retyped by hand still matches.
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

    // A date or number that Excel stores as such leaves as invariant text: the formatted text depends on the regional settings of
    // whoever made the file.
    private static string? Cell(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var raw = cell.DataType switch
        {
            XLDataType.DateTime => DateOnly.FromDateTime(cell.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            XLDataType.Number when DigitsOnlyFormat().IsMatch(cell.Style.NumberFormat.Format) => PaddedDigits(cell),
            XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
