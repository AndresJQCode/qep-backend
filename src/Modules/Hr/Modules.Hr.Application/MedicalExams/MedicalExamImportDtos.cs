namespace Modules.Hr.Application.MedicalExams;

/// <summary>An error of a row: <c>Message</c> is Spanish and is what the frontend shows; <c>Field</c> is the camelCase key, or <c>null</c>.</summary>
public sealed record MedicalExamImportIssueDto(int RowNumber, string Code, string Message, string? Field);

/// <summary>A valid row. It never carries the clinical text, only whether there are restrictions.</summary>
public sealed record MedicalExamImportRowPreviewDto(
    int RowNumber,
    string EmployeeName,
    string DocumentType,
    string DocumentNumber,
    string Type,
    DateOnly ExamDate,
    bool HasRestrictions);

public sealed record MedicalExamImportSummaryDto(int ExamsToCreate);

public sealed record MedicalExamImportPreviewDto(
    string FileName,
    int TotalRows,
    IReadOnlyList<MedicalExamImportRowPreviewDto> ValidRows,
    IReadOnlyList<MedicalExamImportIssueDto> Errors,
    MedicalExamImportSummaryDto Summary);

public sealed record MedicalExamImportedRowDto(int RowNumber, Guid ExamId);

public sealed record MedicalExamImportResultDto(
    string FileName, DateTimeOffset ReceivedAt, int ImportedCount, IReadOnlyList<MedicalExamImportedRowDto> Imported);

public sealed record MedicalExamImportTemplateFile(byte[] Content, string FileName);
