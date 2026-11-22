namespace Modules.Payroll.Application;

/// <summary>Un error de una fila: mismo shape que <c>EmployeeImportRowIssueDto</c> de Hr. <c>Message</c> va en español; <c>Field</c> es la clave camelCase (o nulo).</summary>
public sealed record InputsImportRowIssueDto(int RowNumber, string Code, string Message, string? Field);

/// <param name="Concept">El nombre del concepto tal como lo conoce la empresa.</param>
/// <param name="Kind"><c>Overtime</c>, <c>Income</c> o <c>Deduction</c>.</param>
public sealed record InputsImportRowPreviewDto(
    int RowNumber,
    string DocumentNumber,
    string FullName,
    string Concept,
    string Kind,
    decimal? Hours,
    decimal? Amount,
    bool? SalaryConstitutive);

public sealed record InputsImportSummaryDto(int Employees, int OvertimeLines, int IncomeLines, int DeductionLines);

public sealed record InputsImportPreviewDto(
    string FileName,
    int TotalRows,
    IReadOnlyList<InputsImportRowPreviewDto> ValidRows,
    IReadOnlyList<InputsImportRowIssueDto> Errors,
    InputsImportSummaryDto Summary);

public sealed record InputsImportTemplateFile(byte[] Content, string FileName);
