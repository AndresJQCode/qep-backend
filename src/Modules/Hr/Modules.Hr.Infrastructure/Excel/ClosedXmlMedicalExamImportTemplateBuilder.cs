using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// The exam history template with ClosedXML (same approach as the employee template): the sheet <c>Exámenes</c> with the eight
/// headers and <c>Valores válidos</c> with the two lists, plus the list validation on the two columns that use them, so that a value
/// the import rejects cannot be typed.
/// </summary>
internal sealed class ClosedXmlMedicalExamImportTemplateBuilder : IMedicalExamImportTemplateBuilder
{
    private const double MinimumColumnWidth = 14;

    // Rows with the dropdown and the formats: the cap of rows of the import.
    private const int FormattedRows = MedicalExamImportColumns.MaxRows;

    private static int ColumnOf(string header) => MedicalExamImportColumns.Ordered.ToList().IndexOf(header) + 1;

    public byte[] Build()
    {
        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add(MedicalExamImportColumns.DataSheetName);
        for (var column = 0; column < MedicalExamImportColumns.Ordered.Count; column++)
        {
            data.Cell(1, column + 1).Value = MedicalExamImportColumns.Ordered[column];
        }

        data.SheetView.FreezeRows(1);
        data.Row(1).Style.Font.Bold = true;

        // Dates as dates: Excel offers the picker and the reader receives them in ISO, whatever the regional format.
        foreach (var header in new[] { MedicalExamImportColumns.ExamDate, MedicalExamImportColumns.RestrictionsUntil })
        {
            data.Range(2, ColumnOf(header), FormattedRows + 1, ColumnOf(header)).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        // The document number as text: a leading zero is not lost.
        data.Range(2, ColumnOf(MedicalExamImportColumns.DocumentNumber), FormattedRows + 1, ColumnOf(MedicalExamImportColumns.DocumentNumber))
            .Style.NumberFormat.Format = "@";
        var dataColumns = data.Columns(1, MedicalExamImportColumns.Ordered.Count);
        dataColumns.AdjustToContents();
        ApplyMinimumWidth(dataColumns);

        var reference = workbook.Worksheets.Add(MedicalExamImportColumns.ValuesSheetName);
        (string Title, IReadOnlyList<string> Values, string Header)[] lists =
        [
            (MedicalExamImportColumns.ValuesTitles.DocumentTypes, HrLabels.Of<HrDocumentType>().ToList(), MedicalExamImportColumns.DocumentType),
            (MedicalExamImportColumns.ValuesTitles.ExamTypes, MedicalExamLabels.Types.Values.ToList(), MedicalExamImportColumns.ExamType),
        ];
        for (var list = 0; list < lists.Length; list++)
        {
            var referenceColumn = list + 1;
            reference.Cell(1, referenceColumn).Value = lists[list].Title;
            for (var index = 0; index < lists[list].Values.Count; index++)
            {
                reference.Cell(index + 2, referenceColumn).Value = lists[list].Values[index];
            }

            var letter = reference.Cell(1, referenceColumn).Address.ColumnLetter;
            var column = ColumnOf(lists[list].Header);
            var validation = data.Range(2, column, FormattedRows + 1, column).CreateDataValidation();
            validation.List($"'{MedicalExamImportColumns.ValuesSheetName}'!${letter}$2:${letter}${lists[list].Values.Count + 1}", true);
            validation.IgnoreBlanks = true;
        }

        reference.Row(1).Style.Font.Bold = true;
        var referenceColumns = reference.Columns(1, lists.Length);
        referenceColumns.AdjustToContents();
        ApplyMinimumWidth(referenceColumns);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
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
