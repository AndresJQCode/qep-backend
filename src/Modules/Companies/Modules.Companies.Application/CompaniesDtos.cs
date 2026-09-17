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

public sealed record CompanyCityDto(Guid Id, string DivipolaCode, string Name);

public sealed record CompanyDepartmentDto(Guid Id, string DivipolaCode, string Name);

public sealed record CompanyDto(
    Guid Id,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    bool IsActive,
    string? Phone,
    string? Email,
    string? Address,
    CompanyCityDto City,
    CompanyDepartmentDto Department,
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
    CompanyCityDto City,
    CompanyDepartmentDto Department,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// La fila del listado. Es un subconjunto a proposito: <c>email</c> y <c>address</c> no se pintan
/// en la grilla, y mandarlos multiplica el cuerpo de la respuesta por cada empresa del tenant sin
/// que nadie los mire. <c>City</c> es la excepcion — nombre solo, sin el par completo con su
/// departamento, porque la grilla sí muestra una columna "Ciudad" y evitarla obligaría a otra
/// consulta desde el detalle sólo para llenarla.
///
/// De las cuentas viajan **banco y numero**, sin la moneda, por esa misma razon: la columna de la
/// grilla pinta el banco junto a cada numero para distinguir cuentas de bancos distintos, pero la
/// moneda no se muestra, y mandarla en hasta veinte cuentas por empresa engorda el cuerpo para
/// que el consumidor la descarte. El detalle completo lo trae <c>GET /{companyId}</c>, que es la
/// pantalla donde la moneda si se lee.
///
/// <c>AccountNumbers</c> repite los numeros de <c>BankAccounts</c> a proposito: es el contrato
/// anterior, y quitarlo en el mismo cambio obliga a desplegar frontend y backend a la vez. Se va
/// cuando ningun consumidor desplegado lo lea.
/// </summary>
public sealed record CompanyListItemResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> AccountNumbers,
    IReadOnlyList<CompanyListBankAccount> BankAccounts,
    string TaxId,
    string? Phone,
    string City,
    bool IsActive);

/// <summary>
/// Una cuenta en la fila del listado: el subconjunto de <see cref="CompanyBankAccountPayload"/>
/// que la grilla pinta. Ver <see cref="CompanyListItemResponse"/>.
/// </summary>
public sealed record CompanyListBankAccount(string BankName, string AccountNumber);

public sealed record CompaniesResponse(IReadOnlyCollection<CompanyListItemResponse> Items);

// IsActive no viaja en los requests: una empresa nace activa y solo cambia por /deactivate y
// /activate. Un booleano editable convertiria la desactivacion en un PUT comun y la dejaria sin
// su propia entrada de auditoria, el mismo razonamiento que mantuvo suspender aparte de editar
// roles en AUTH-06 y que fijo el contrato de producto en CAT-02b.
//
// Los tres opcionales si viajan. En el PUT, mandarlos en null los **limpia** — el verbo reemplaza
// el recurso entero. BankAccounts sigue la misma regla y de forma mas visible: la lista que llega
// es la lista que queda, asi que quitar una cuenta es mandar el PUT sin ella.
//
// CityId, en cambio, no se puede limpiar — es obligatorio, mismo criterio que en Customer:
// una empresa siempre tiene una ciudad.
public sealed record CreateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    Guid CityId,
    string? Phone,
    string? Email,
    string? Address);

public sealed record UpdateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    Guid CityId,
    string? Phone,
    string? Email,
    string? Address);
