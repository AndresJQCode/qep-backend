namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// A row of the exam history Excel, with no business validation: the raw trimmed text of each cell by header of
/// <see cref="MedicalExamImportColumns"/> (empty means <c>null</c>). Dates and numbers that Excel stores as such arrive as invariant
/// text (<c>yyyy-MM-dd</c>). <c>RowNumber</c> is the real Excel row (1 is the header).
/// </summary>
public sealed record ExcelMedicalExamRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells);

/// <summary>
/// Reads the exam history Excel (ClosedXML adapter in Infrastructure): only structural parsing of the first sheet, locating the eight
/// columns by header. An unreadable file throws <c>hr.import.unreadable</c> and a missing header <c>hr.import.header_mismatch</c>.
/// Blank rows are skipped.
/// </summary>
public interface IExcelMedicalExamImporter
{
    Task<IReadOnlyList<ExcelMedicalExamRow>> ReadAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Builds the <c>.xlsx</c> template: the sheet <c>Exámenes</c> with the eight headers and <c>Valores válidos</c> with the lists.</summary>
public interface IMedicalExamImportTemplateBuilder
{
    byte[] Build();
}
