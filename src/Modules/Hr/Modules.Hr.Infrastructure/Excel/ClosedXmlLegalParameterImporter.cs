using System.Globalization;
using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// Lee el Excel de valores legales con ClosedXML (calco de <c>ClosedXmlEmployeeImporter</c>). Sólo parseo estructural: busca las hojas
/// «Parámetros» y «Tramos» por nombre y sus columnas por encabezado, no por posición, y devuelve el texto crudo de cada celda. Las
/// reglas viven en Application.
/// </summary>
internal sealed class ClosedXmlLegalParameterImporter : IExcelLegalParameterImporter
{
    public Task<LegalParameterWorkbook> ReadAsync(Stream content, CancellationToken cancellationToken)
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
            throw new HrDomainException(
                HrErrorCodes.LegalParameterImportUnreadable, "No pudimos leer el archivo como un libro de Excel.");
        }

        using (workbook)
        {
            var parameters = ReadSheet(
                workbook, LegalParameterImportColumns.ParametersSheet, LegalParameterImportColumns.RequiredParameterColumns, cancellationToken)
                .Select(row => new ExcelLegalParameterRow(
                    row.Number,
                    row.Cells[LegalParameterImportColumns.Key],
                    row.Cells[LegalParameterImportColumns.EffectiveFrom],
                    row.Cells[LegalParameterImportColumns.NewValue]))
                .ToList();
            var brackets = ReadSheet(
                workbook, LegalParameterImportColumns.BracketsSheet, LegalParameterImportColumns.RequiredBracketColumns, cancellationToken)
                .Select(row => new ExcelLegalBracketRow(
                    row.Number,
                    row.Cells[LegalParameterImportColumns.Key],
                    row.Cells[LegalParameterImportColumns.EffectiveFrom],
                    row.Cells[LegalParameterImportColumns.LowerBound],
                    row.Cells[LegalParameterImportColumns.UpperBound],
                    row.Cells[LegalParameterImportColumns.Rate]))
                .ToList();
            return Task.FromResult(new LegalParameterWorkbook(parameters, brackets));
        }
    }

    private sealed record SheetRow(int Number, Dictionary<string, string?> Cells);

    private static List<SheetRow> ReadSheet(
        XLWorkbook workbook, string sheetName, IReadOnlyList<string> required, CancellationToken cancellationToken)
    {
        var wanted = HrText.Fold(sheetName);
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet => HrText.Fold(sheet.Name) == wanted)
            ?? throw Mismatch();

        var columnByName = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in required)
        {
            if (!columnByName.TryGetValue(HrText.Fold(expected)!, out var index))
            {
                throw Mismatch();
            }

            indexes[expected] = index;
        }

        var rows = new List<SheetRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = worksheet.Row(rowNumber);
            var cells = required.ToDictionary(column => column, column => Cell(row, indexes[column]), StringComparer.Ordinal);

            // Una fila en blanco entre los datos es ruido de formato, no una fila que reportar.
            if (cells.Values.All(cell => cell is null))
            {
                continue;
            }

            rows.Add(new SheetRow(rowNumber, cells));
        }

        return rows;
    }

    private static HrDomainException Mismatch() =>
        new(
            HrErrorCodes.LegalParameterImportHeaderMismatch,
            "Las hojas o las columnas no coinciden con la plantilla. Descarga la plantilla de nuevo.");

    // Sin mayúsculas ni tildes: «Parametros» retipeado a mano es «Parámetros».
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
    /// formateado depende de la configuración regional de quien hizo el archivo. Una celda con formato de porcentaje trae la fracción
    /// (4 % es <c>0.04</c>), que es lo que se guarda.
    /// </summary>
    private static string? Cell(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var raw = cell.DataType switch
        {
            XLDataType.DateTime => DateOnly.FromDateTime(cell.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
