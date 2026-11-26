namespace Modules.Hr.Application;

/// <summary>
/// Un error o un aviso de una fila: mismo shape que <c>ImportRowErrorDto</c> de Contratos de servicio. <c>Message</c> viene en español
/// y es lo que muestra el frontend; <c>Field</c> es la clave camelCase del campo (o <c>null</c>).
/// </summary>
public sealed record EmployeeImportRowIssueDto(int RowNumber, string Code, string Message, string? Field);

/// <param name="Company">El nombre de la empresa empleadora, sin el NIT.</param>
/// <param name="Workplace">«centro - sede».</param>
/// <param name="ContractStatus"><c>Active</c> o <c>Terminated</c> (con fecha de retiro pasada).</param>
public sealed record EmployeeImportRowPreviewDto(
    int RowNumber,
    string FirstName,
    string LastName,
    string DocumentType,
    string DocumentNumber,
    string Company,
    string ContractType,
    string Duration,
    string Position,
    string Workplace,
    decimal Salary,
    DateOnly StartDate,
    DateOnly? EndDate,
    string ContractStatus);

public sealed record EmployeeImportSummaryDto(int EmployeesToCreate, int ActiveContracts, int TerminatedContracts);

public sealed record EmployeeImportPreviewDto(
    string FileName,
    int TotalRows,
    IReadOnlyList<EmployeeImportRowPreviewDto> ValidRows,
    IReadOnlyList<EmployeeImportRowIssueDto> Errors,
    IReadOnlyList<EmployeeImportRowIssueDto> Warnings,
    EmployeeImportSummaryDto Summary);

public sealed record EmployeeImportedRowDto(int RowNumber, Guid EmployeeId);

public sealed record EmployeeImportResultDto(
    string FileName,
    DateTimeOffset ReceivedAt,
    int ImportedCount,
    IReadOnlyList<EmployeeImportedRowDto> Imported);

public sealed record EmployeeImportTemplateFile(byte[] Content, string FileName);
