using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Lee un Excel de clientes con ClosedXML. Solo parseo estructural: ubica las trece columnas
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
            if (columnByName.TryGetValue(expected, out var index))
            {
                indexes[expected] = index;
            }
            else if (!CustomerImportColumns.WithholdingTypes.Contains(expected))
            {
                return new ExcelCustomerImportFile(false, []);
            }
        }

        // Un archivo anterior a las columnas por tipo no las trae, o trae sólo una: no es lo mismo que traerla vacía (que quita
        // la tasa de ese tipo). Se cuenta por columna.
        string[] withholdingColumns = [.. CustomerImportColumns.WithholdingTypes.Where(indexes.ContainsKey)];

        // Las opcionales no hacen inválido el archivo si faltan: un Excel anterior a la columna «Empresa» se lee como «sin empresa».
        foreach (var optional in CustomerImportColumns.Optional)
        {
            if (columnByName.TryGetValue(optional, out var index))
            {
                indexes[optional] = index;
            }
        }
        var rows = new List<ExcelCustomerRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = worksheet.Row(rowNumber);
            var cuc = Cell(row, indexes, CustomerImportColumns.Cuc);
            var name = Cell(row, indexes, CustomerImportColumns.Name);
            var businessName = Cell(row, indexes, CustomerImportColumns.BusinessName);
            var identificationType = Cell(row, indexes, CustomerImportColumns.IdentificationType);
            var identificationNumber = Cell(row, indexes, CustomerImportColumns.IdentificationNumber);
            var phone = Cell(row, indexes, CustomerImportColumns.Phone);
            var email = Cell(row, indexes, CustomerImportColumns.Email);
            var address = Cell(row, indexes, CustomerImportColumns.Address);
            var department = Cell(row, indexes, CustomerImportColumns.Department);
            var city = Cell(row, indexes, CustomerImportColumns.City);
            var classification = Cell(row, indexes, CustomerImportColumns.Classification);
            var retefuente = Cell(row, indexes, CustomerImportColumns.Retefuente);
            var reteIca = Cell(row, indexes, CustomerImportColumns.ReteIca);
            var vatSurplus = Cell(row, indexes, CustomerImportColumns.VatSurplus);
            var company = indexes.ContainsKey(CustomerImportColumns.Company)
                ? Cell(row, indexes, CustomerImportColumns.Company)
                : null;

            // Una fila completamente vacia es ruido de formato (una fila en blanco que Excel deja
            // entre los datos y el final de la hoja), no una fila de datos que haya que reportar
            // como invalida.
            if (cuc is null && name is null && businessName is null &&
                identificationType is null &&
                identificationNumber is null && phone is null && email is null &&
                address is null && department is null && city is null &&
                classification is null && retefuente is null && reteIca is null && vatSurplus is null && company is null)
            {
                continue;
            }

            rows.Add(new ExcelCustomerRow(
                rowNumber,
                cuc,
                name,
                businessName,
                identificationType,
                identificationNumber,
                phone,
                email,
                address,
                department,
                city,
                classification,
                retefuente,
                reteIca,
                vatSurplus,
                company));
        }

        return new ExcelCustomerImportFile(true, rows, withholdingColumns);
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
        if (!indexes.TryGetValue(column, out var index))
        {
            return null;
        }

        var raw = row.Cell(index).GetString().Trim();
        return raw.Length == 0 ? null : raw;
    }
}
