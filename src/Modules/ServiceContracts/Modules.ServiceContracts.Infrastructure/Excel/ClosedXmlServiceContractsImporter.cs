using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Excel;

/// <summary>
/// Lee el Excel de cobros con ClosedXML (copia de <c>ClosedXmlCustomerImporter</c>). Sólo parseo estructural: ubica las trece
/// columnas por su encabezado, no por posición, y devuelve el texto crudo de cada celda. Las reglas viven en Application.
/// </summary>
internal sealed class ClosedXmlServiceContractsImporter : IExcelServiceContractsImporter
{
    public Task<IReadOnlyList<ExcelServiceContractsRow>> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un archivo corrupto, protegido o que no es un .xlsx (un .csv renombrado) revienta ClosedXML con excepciones de bajo
            // nivel que no le dicen nada a quien lo subió: el archivo no se puede leer, y eso es todo lo que hay que decir.
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ImportUnreadable, "The file could not be read as an Excel workbook.");
        }

        using (workbook)
        {
            return Task.FromResult<IReadOnlyList<ExcelServiceContractsRow>>(Read(workbook.Worksheets.First(), cancellationToken));
        }
    }

    private static List<ExcelServiceContractsRow> Read(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columnByName = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in ServiceContractsImportColumns.Ordered)
        {
            if (!columnByName.TryGetValue(Fold(expected), out var index))
            {
                throw new ServiceContractsDomainException(
                    ServiceContractsErrorCodes.ImportHeaderMismatch,
                    "The file does not have the template's columns. Download the template and try again.");
            }

            indexes[expected] = index;
        }

        var rows = new List<ExcelServiceContractsRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = worksheet.Row(rowNumber);
            var cells = ServiceContractsImportColumns.Ordered.Select(column => Cell(row, indexes[column])).ToArray();

            // Una fila en blanco entre los datos es ruido de formato, no una fila que reportar.
            if (cells.All(cell => cell is null))
            {
                continue;
            }

            rows.Add(new ExcelServiceContractsRow(
                rowNumber,
                cells[0],
                cells[1],
                cells[2],
                cells[3],
                cells[4],
                cells[5],
                cells[6],
                cells[7],
                cells[8],
                cells[9],
                cells[10],
                cells[11],
                cells[12]));
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
            var name = Fold(header.Cell(column).GetString());
            if (name.Length > 0)
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
            XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }
}
