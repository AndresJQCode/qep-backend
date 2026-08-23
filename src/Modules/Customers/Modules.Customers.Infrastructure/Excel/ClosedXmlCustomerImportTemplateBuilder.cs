using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Arma la plantilla de importacion con ClosedXML: la primera hoja con las diez columnas de
/// <see cref="CustomerImportColumns"/> en la cabecera, y una segunda hoja de referencia con los
/// nombres de departamento y de clasificacion validos — para que quien llena el Excel no tenga que
/// adivinarlos ni pedirlos por otro canal.
/// </summary>
internal sealed class ClosedXmlCustomerImportTemplateBuilder : ICustomerImportTemplateBuilder
{
    private const string DataSheetName = "Clientes";

    private const string ReferenceSheetName = "Referencia";

    public byte[] Build(
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();

        var dataSheet = workbook.Worksheets.Add(DataSheetName);
        for (var column = 0; column < CustomerImportColumns.Ordered.Count; column++)
        {
            dataSheet.Cell(1, column + 1).Value = CustomerImportColumns.Ordered[column];
        }

        dataSheet.SheetView.FreezeRows(1);
        dataSheet.Row(1).Style.Font.Bold = true;
        dataSheet.Columns(1, CustomerImportColumns.Ordered.Count).AdjustToContents();

        cancellationToken.ThrowIfCancellationRequested();
        BuildReferenceSheet(workbook, departmentNames, classificationNames);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildReferenceSheet(
        XLWorkbook workbook,
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames)
    {
        var reference = workbook.Worksheets.Add(ReferenceSheetName);
        reference.Cell(1, 1).Value = "Departamentos validos";
        reference.Cell(1, 2).Value = "Clasificaciones validas";
        reference.Row(1).Style.Font.Bold = true;

        var departments = departmentNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var classifications = classificationNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var rowCount = Math.Max(departments.Length, classifications.Length);
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
        }

        reference.Columns(1, 2).AdjustToContents();
    }
}
