namespace Modules.ServiceContracts.Application;

// Contrato HTTP de cobros-recurrentes §4 «Importación». Opcionales como null, fechas yyyy-MM-dd.

public sealed record ImportRowErrorDto(int RowNumber, string Code, string Message, string? Field);

/// <summary><c>Action</c> es <c>create</c> (se da de alta) o <c>match</c> (<c>MatchedId</c> es el cliente emparejado).</summary>
public sealed record ImportCustomerPreviewDto(string Name, Guid? MatchedId, string Action);

public sealed record ImportServicePreviewDto(string Name, decimal Price, string Currency, string Cycle, DateOnly FirstDueDate);

/// <summary><c>ProfileAction</c>: <c>create</c> si el cliente no tiene perfil, <c>kept</c> si ya tiene (canal y encargado se ignoran).</summary>
public sealed record ImportRowPreviewDto(
    int RowNumber,
    ImportCustomerPreviewDto Customer,
    string ProfileAction,
    ImportServicePreviewDto Service,
    int ChargesToGenerate,
    int OverdueCharges);

public sealed record ImportPreviewSummaryDto(
    int CustomersToCreate,
    int CustomersMatched,
    int ServicesToCreate,
    int ChargesToGenerate,
    int OverdueCharges);

public sealed record ImportPreviewDto(
    string FileName,
    int TotalRows,
    IReadOnlyList<ImportRowPreviewDto> ValidRows,
    IReadOnlyList<ImportRowErrorDto> Errors,
    ImportPreviewSummaryDto Summary);

public sealed record ImportedRowDto(
    int RowNumber,
    Guid ServiceId,
    Guid CustomerId,
    bool CustomerCreated,
    int ChargesGenerated,
    int OverdueCharges);

/// <summary><c>ErrorCount</c> cuenta filas con error, no errores: una fila con dos reglas rotas es una fila que no entró.</summary>
public sealed record ImportResultDto(
    string FileName,
    DateTimeOffset ReceivedAt,
    int TotalRows,
    int ImportedCount,
    int ErrorCount,
    int CustomersCreated,
    IReadOnlyList<ImportedRowDto> Imported,
    IReadOnlyList<ImportRowErrorDto> Errors);

public sealed record ServiceContractsImportTemplateFile(byte[] Content, string FileName);
