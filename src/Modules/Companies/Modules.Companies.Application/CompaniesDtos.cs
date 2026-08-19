namespace Modules.Companies.Application;

public sealed record CompanyDto(
    Guid Id,
    string Name,
    string AccountNumber,
    string TaxId,
    bool IsActive,
    string? Phone,
    string? Email,
    string? Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CompanyResponse(
    Guid Id,
    string Name,
    string AccountNumber,
    string TaxId,
    bool IsActive,
    string? Phone,
    string? Email,
    string? Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// La fila del listado. Es un subconjunto a proposito: <c>email</c> y <c>address</c> no se pintan
/// en la grilla, y mandarlos multiplica el cuerpo de la respuesta por cada empresa del tenant sin
/// que nadie los mire. La forma la fija el consumidor que ya existe,
/// <c>CompanyListItemDto</c> en <c>features/companies/types/company-list.ts</c>.
/// </summary>
public sealed record CompanyListItemResponse(
    Guid Id,
    string Name,
    string AccountNumber,
    string TaxId,
    string? Phone,
    bool IsActive);

public sealed record CompaniesResponse(IReadOnlyCollection<CompanyListItemResponse> Items);

// IsActive no viaja en los requests: una empresa nace activa y solo cambia por /deactivate y
// /activate. Un booleano editable convertiria la desactivacion en un PUT comun y la dejaria sin
// su propia entrada de auditoria, el mismo razonamiento que mantuvo suspender aparte de editar
// roles en AUTH-06 y que fijo el contrato de producto en CAT-02b.
//
// Los tres opcionales si viajan. En el PUT, mandarlos en null los **limpia** — el verbo reemplaza
// el recurso entero.
public sealed record CreateCompanyRequest(
    string Name,
    string AccountNumber,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address);

public sealed record UpdateCompanyRequest(
    string Name,
    string AccountNumber,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address);
