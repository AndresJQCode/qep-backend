namespace Modules.Companies.Application;

/// <summary>
/// Una cuenta bancaria en el contrato HTTP. Sirve de ida y de vuelta: el mismo tipo viaja en el
/// POST, en el PUT y en la respuesta, porque las tres formas son identicas y tener tres records
/// gemelos solo garantiza que algun dia difieran.
///
/// Posicional, a diferencia de <c>CompanyBankAccount</c> en el dominio: aca no hay riesgo de
/// intercambiar argumentos sin querer porque nadie lo construye a mano — lo deserializa
/// System.Text.Json por nombre de propiedad.
/// </summary>
public sealed record CompanyBankAccountPayload(
    string BankName,
    string AccountNumber,
    string Currency);

public sealed record CompanyDto(
    Guid Id,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
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
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
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
/// que nadie los mire.
///
/// De las cuentas viajan **solo los numeros**, y no la terna completa, por esa misma razon: la
/// columna de la grilla pinta el numero —es lo unico que pintaba cuando era un campo plano— y
/// mandar banco y moneda de hasta veinte cuentas por empresa multiplica el cuerpo por veinte para
/// que el consumidor descarte dos tercios. El detalle completo lo trae <c>GET /{companyId}</c>,
/// que es la pantalla donde esos datos si se leen.
/// </summary>
public sealed record CompanyListItemResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> AccountNumbers,
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
// el recurso entero. BankAccounts sigue la misma regla y de forma mas visible: la lista que llega
// es la lista que queda, asi que quitar una cuenta es mandar el PUT sin ella.
public sealed record CreateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address);

public sealed record UpdateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address);
