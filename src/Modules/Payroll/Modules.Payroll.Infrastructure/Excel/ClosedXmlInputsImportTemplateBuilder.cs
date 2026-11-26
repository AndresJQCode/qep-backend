using ClosedXML.Excel;
using Modules.Payroll.Application;

namespace Modules.Payroll.Infrastructure.Excel;

/// <summary>
/// La plantilla de novedades con ClosedXML: la hoja <c>Novedades</c> con los seis encabezados y listas desplegables en
/// <c>CONCEPTO</c> y <c>CONSTITUTIVO DE SALARIO</c>, la hoja <c>Empleados</c> con los que se pueden capturar y la hoja
/// <c>Valores válidos</c> con los conceptos vivos y Sí / No.
/// </summary>
internal sealed class ClosedXmlInputsImportTemplateBuilder : IInputsImportTemplateBuilder
{
    private const double MinimumColumnWidth = 14;

    // Rows with a drop-down list: the importation's row limit.
    private const int ValidationRows = InputsImportColumns.MaxRows;

    private static int ColumnOf(string header) => InputsImportColumns.Ordered.ToList().IndexOf(header) + 1;

    public byte[] Build(InputsImportTemplateData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(InputsImportColumns.DataSheetName);
        for (var column = 0; column < InputsImportColumns.Ordered.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = InputsImportColumns.Ordered[column];
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;

        // A document as text: a leading zero is not lost.
        sheet.Range(2, ColumnOf(InputsImportColumns.DocumentNumber), ValidationRows + 1, ColumnOf(InputsImportColumns.DocumentNumber))
            .Style.NumberFormat.Format = "@";

        var employees = workbook.Worksheets.Add(InputsImportColumns.EmployeesSheetName);
        employees.Cell(1, 1).Value = InputsImportColumns.DocumentNumber;
        employees.Cell(1, 2).Value = InputsImportColumns.Employee;
        for (var index = 0; index < data.Employees.Count; index++)
        {
            employees.Cell(index + 2, 1).SetValue(data.Employees[index].DocumentNumber);
            employees.Cell(index + 2, 2).SetValue(data.Employees[index].FullName);
        }

        employees.Row(1).Style.Font.Bold = true;

        var values = workbook.Worksheets.Add(InputsImportColumns.ValuesSheetName);
        values.Cell(1, 1).Value = InputsImportColumns.ConceptsListTitle;
        for (var index = 0; index < data.ConceptNames.Count; index++)
        {
            values.Cell(index + 2, 1).SetValue(data.ConceptNames[index]);
        }

        values.Cell(1, 2).Value = InputsImportColumns.YesNoListTitle;
        values.Cell(2, 2).SetValue(PayrollLabels.Yes);
        values.Cell(3, 2).SetValue(PayrollLabels.No);
        values.Row(1).Style.Font.Bold = true;

        if (data.ConceptNames.Count > 0)
        {
            var concepts = sheet.Range(2, ColumnOf(InputsImportColumns.Concept), ValidationRows + 1, ColumnOf(InputsImportColumns.Concept))
                .CreateDataValidation();
            concepts.List($"'{InputsImportColumns.ValuesSheetName}'!$A$2:$A${data.ConceptNames.Count + 1}", true);
            concepts.IgnoreBlanks = true;
        }

        var classification = sheet
            .Range(2, ColumnOf(InputsImportColumns.SalaryConstitutive), ValidationRows + 1, ColumnOf(InputsImportColumns.SalaryConstitutive))
            .CreateDataValidation();
        classification.List($"'{InputsImportColumns.ValuesSheetName}'!$B$2:$B$3", true);
        classification.IgnoreBlanks = true;

        foreach (var worksheet in new[] { sheet, employees, values })
        {
            var columns = worksheet.Columns(1, worksheet == sheet ? InputsImportColumns.Ordered.Count : 2);
            columns.AdjustToContents();
            foreach (var column in columns.Where(column => column.Width < MinimumColumnWidth))
            {
                column.Width = MinimumColumnWidth;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
