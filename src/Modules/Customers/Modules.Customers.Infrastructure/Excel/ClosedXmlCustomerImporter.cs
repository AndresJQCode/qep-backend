using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Lee un Excel de clientes con ClosedXML. Solo parseo estructural: ubica las diez columnas
/// esperadas por su nombre de cabecera (no por posicion — una persona reordenando columnas en
/// Excel es mas probable que reordenar texto) y lee las filas de datos como texto crudo. Ninguna
/// regla de negocio vive aca; eso es <c>ExcelCustomerRowRules</c> y <c>ImportCustomersHandler</c>,
/// en Application.
/// </summary>
internal sealed class ClosedXmlCustomerImporter : IExcelCustomerImporter
{
    public ExcelCustomerImportFile Parse(Stream content, CancellationToken cancellationToken)
    {
        try
        {
            using var workbook = new XLWorkbook(content);
            var worksheet = workbook.Worksheets.First();
            return ParseWorksheet(worksheet, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un archivo corrupto, protegido con contrasena, o que no es realmente un .xlsx (un
            // .csv renombrado, por ejemplo) revienta ClosedXML con excepciones de bajo nivel que no
            // le dicen nada util a quien subio el archivo. Se homogeneiza a "no tiene las columnas
            // esperadas": desde afuera del importador es indistinguible de una cabecera equivocada,
            // y los dos son "el archivo no sirve" — el mismo 422 de archivo invalido.
            return new ExcelCustomerImportFile(false, []);
        }
    }

    private static ExcelCustomerImportFile ParseWorksheet(
        IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columnByName = MapHeaderColumns(worksheet);

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in CustomerImportColumns.Ordered)
        {
            if (!columnByName.TryGetValue(expected, out var index))
            {
                return new ExcelCustomerImportFile(false, []);
            }

            indexes[expected] = index;
        }

        var rows = new List<ExcelCustomerRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = worksheet.Row(rowNumber);
            var name = Cell(row, indexes, CustomerImportColumns.Name);
            var identificationType = Cell(row, indexes, CustomerImportColumns.IdentificationType);
            var identificationNumber = Cell(row, indexes, CustomerImportColumns.IdentificationNumber);
            var phone = Cell(row, indexes, CustomerImportColumns.Phone);
            var email = Cell(row, indexes, CustomerImportColumns.Email);
            var address = Cell(row, indexes, CustomerImportColumns.Address);
            var department = Cell(row, indexes, CustomerImportColumns.Department);
            var city = Cell(row, indexes, CustomerImportColumns.City);
            var classification = Cell(row, indexes, CustomerImportColumns.Classification);
            var withRetention = Cell(row, indexes, CustomerImportColumns.WithRetention);

            // Una fila completamente vacia es ruido de formato (una fila en blanco que Excel deja
            // entre los datos y el final de la hoja), no una fila de datos que haya que reportar
            // como invalida.
            if (name is null && identificationType is null && identificationNumber is null &&
                phone is null && email is null && address is null && department is null &&
                city is null && classification is null && withRetention is null)
            {
                continue;
            }

            rows.Add(new ExcelCustomerRow(
                rowNumber,
                name,
                identificationType,
                identificationNumber,
                phone,
                email,
                address,
                department,
                city,
                classification,
                withRetention));
        }

        return new ExcelCustomerImportFile(true, rows);
    }

    private static Dictionary<string, int> MapHeaderColumns(IXLWorksheet worksheet)
    {
        var headerRow = worksheet.Row(1);
        var lastColumn = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var columnByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var column = 1; column <= lastColumn; column++)
        {
            var name = headerRow.Cell(column).GetString().Trim();
            if (!string.IsNullOrEmpty(name) && !columnByName.ContainsKey(name))
            {
                columnByName[name] = column;
            }
        }

        return columnByName;
    }

    private static string? Cell(IXLRow row, Dictionary<string, int> indexes, string column)
    {
        var raw = row.Cell(indexes[column]).GetString().Trim();
        return raw.Length == 0 ? null : raw;
    }
}
