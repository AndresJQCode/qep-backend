using System.Globalization;
using ClosedXML.Excel;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Excel;

/// <summary>
/// Lee la hoja de carga inicial con ClosedXML. Sólo parseo estructural: ubica las seis columnas por su cabecera (no por posición) y
/// devuelve cada fila como texto crudo. Las celdas numéricas y de fecha se vuelven texto invariante (<c>12.5</c>, <c>2027-03-31</c>)
/// para que la regla de la fila no dependa de la cultura de quien guardó el archivo.
/// </summary>
internal sealed class ClosedXmlInitialBalanceSheetReader : IInitialBalanceSheetReader
{
    public InitialBalanceSheet Parse(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            using var workbook = new XLWorkbook(content);
            return ParseWorksheet(workbook.Worksheets.First(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un archivo corrupto, protegido o que no es un .xlsx revienta ClosedXML con excepciones de bajo nivel que no le dicen nada a
            // quien lo subió: desde afuera es indistinguible de una cabecera equivocada.
            return new InitialBalanceSheet(false, []);
        }
    }

    private static InitialBalanceSheet ParseWorksheet(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columns = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in InitialBalanceColumns.Ordered)
        {
            if (!columns.TryGetValue(expected, out var index))
            {
                return new InitialBalanceSheet(false, []);
            }

            indexes[expected] = index;
        }

        var rows = new List<InitialBalanceSheetRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = worksheet.Row(rowNumber);
            var code = Text(row, indexes[InitialBalanceColumns.Code]);
            var location = Text(row, indexes[InitialBalanceColumns.Location]);
            var lot = Text(row, indexes[InitialBalanceColumns.Lot]);
            var expiration = Text(row, indexes[InitialBalanceColumns.Expiration]);
            var quantity = Text(row, indexes[InitialBalanceColumns.Quantity]);
            var uom = Text(row, indexes[InitialBalanceColumns.Uom]);

            // Una fila completamente vacía es ruido de formato, no una fila de datos que haya que reportar como inválida.
            if (code is null && location is null && lot is null && expiration is null && quantity is null && uom is null)
            {
                continue;
            }

            rows.Add(new InitialBalanceSheetRow(rowNumber, code, location, lot, expiration, quantity, uom));
        }

        return new InitialBalanceSheet(true, rows);
    }

    private static Dictionary<string, int> MapHeaderColumns(IXLWorksheet worksheet)
    {
        var header = worksheet.Row(1);
        var lastColumn = header.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var column = 1; column <= lastColumn; column++)
        {
            var name = header.Cell(column).GetString().Trim();
            if (!string.IsNullOrEmpty(name) && !columns.ContainsKey(name))
            {
                columns[name] = column;
            }
        }

        return columns;
    }

    private static string? Text(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var text = cell.DataType switch
        {
            XLDataType.Number => Convert.ToDecimal(cell.GetDouble(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => cell.GetString().Trim(),
        };

        return text.Length == 0 ? null : text;
    }
}
