using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Arma la plantilla de importacion con ClosedXML: la primera hoja con las diez columnas de
/// <see cref="CustomerImportColumns"/> en la cabecera, y una segunda hoja de referencia con los
/// nombres de departamento, de clasificacion y los tipos de identificacion validos — para que
/// quien llena el Excel no tenga que adivinarlos ni pedirlos por otro canal.
/// </summary>
internal sealed class ClosedXmlCustomerImportTemplateBuilder : ICustomerImportTemplateBuilder
{
    private const string DataSheetName = "Clientes";

    private const string ReferenceSheetName = "Referencia";

    // `AdjustToContents()` ajusta al ancho exacto del texto, sin margen: la cabecera queda pegada
    // al borde de la celda siguiente y es incomoda de leer. Este piso deja aire aunque el nombre
    // de la columna sea corto (ej. "Ciudad").
    private const double MinimumColumnWidth = 14;

    public byte[] Build(
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        CancellationToken cancellationToken) =>
        BuildWithRows(
            departmentNames, classificationNames, identificationTypeValues, [], cancellationToken);

    public byte[] BuildWithRows(
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        IReadOnlyList<CustomerImportRowData> rows,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();

        var dataSheet = workbook.Worksheets.Add(DataSheetName);
        for (var column = 0; column < CustomerImportColumns.Ordered.Count; column++)
        {
            dataSheet.Cell(1, column + 1).Value = CustomerImportColumns.Ordered[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            WriteRow(dataSheet, index + 2, rows[index]);
        }

        dataSheet.SheetView.FreezeRows(1);
        dataSheet.Row(1).Style.Font.Bold = true;
        var dataColumns = dataSheet.Columns(1, CustomerImportColumns.Ordered.Count);
        dataColumns.AdjustToContents();
        ApplyMinimumWidth(dataColumns);

        cancellationToken.ThrowIfCancellationRequested();
        BuildReferenceSheet(workbook, departmentNames, classificationNames, identificationTypeValues);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // El mismo orden que `CustomerImportColumns.Ordered` — una fila desalineada de su cabecera
    // sería peor que no tener datos precargados.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerImportRowData row)
    {
        sheet.Cell(excelRow, 1).Value = row.Name;
        sheet.Cell(excelRow, 2).Value = row.IdentificationType;
        sheet.Cell(excelRow, 3).Value = row.IdentificationNumber;
        sheet.Cell(excelRow, 4).Value = row.Phone ?? string.Empty;
        sheet.Cell(excelRow, 5).Value = row.Email ?? string.Empty;
        sheet.Cell(excelRow, 6).Value = row.Address ?? string.Empty;
        sheet.Cell(excelRow, 7).Value = row.Department;
        sheet.Cell(excelRow, 8).Value = row.City;
        sheet.Cell(excelRow, 9).Value = row.Classification;
        sheet.Cell(excelRow, 10).Value = row.WithRetention ?? string.Empty;
    }

    private static void BuildReferenceSheet(
        XLWorkbook workbook,
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues)
    {
        var reference = workbook.Worksheets.Add(ReferenceSheetName);
        reference.Cell(1, 1).Value = "Departamentos validos";
        reference.Cell(1, 2).Value = "Clasificaciones validas";
        reference.Cell(1, 3).Value = "Tipos de identificacion validos";
        reference.Row(1).Style.Font.Bold = true;

        var departments = departmentNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var classifications = classificationNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var identificationTypes = identificationTypeValues
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var rowCount = Math.Max(departments.Length, Math.Max(classifications.Length, identificationTypes.Length));
        for (var index = 0; index < rowCount; index++)
        {
            var row = index + 2;
            if (index < departments.Length)
            {
                reference.Cell(row, 1).Value = departments[index];
            }

            if (index < classifications.Length)
            {
                reference.Cell(row, 2).Value = classifications[index];
            }

            if (index < identificationTypes.Length)
            {
                reference.Cell(row, 3).Value = identificationTypes[index];
            }
        }

        var referenceColumns = reference.Columns(1, 3);
        referenceColumns.AdjustToContents();
        ApplyMinimumWidth(referenceColumns);
    }

    private static void ApplyMinimumWidth(IXLColumns columns)
    {
        foreach (var column in columns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }
    }
}
