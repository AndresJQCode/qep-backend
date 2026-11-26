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

/// <summary>
/// La referencia de municipio del spec ReteICA por empresa (§9.1): la misma forma en tasas, empresas, clientes y documentos.
/// No reusa <see cref="CompanyCityDto"/>, que trae el código DIVIPOLA y no el departamento.
/// </summary>
public sealed record CompanyMunicipalityDto(Guid Id, string Name, Guid DepartmentId, string DepartmentName);

/// <summary>
/// Una tasa municipal (ReteICA) asignada a la empresa, con su municipio resuelto (backend for frontend): la ficha pinta
/// «ReteICA Bogotá · Bogotá, D.C. · 0,966 %» sin pedirle nada a Catalog ni a Geography. <c>City</c> es null sólo si la
/// ciudad ya no se encontró.
/// </summary>
/// <param name="AppliesTo">El nombre de <c>WithholdingScope</c> de la tasa: <c>All</c>, <c>Goods</c> o <c>Services</c> (spec
/// 2026-11-18 servicios pendientes §2). Viaja con la tasa guardada para que la ficha diga «ReteICA (servicios)» aunque la
/// tasa ya no esté en el catálogo activo que tiene la pantalla; sin él tendrías que cruzar el id con ese catálogo, y una
/// tasa desactivada se quedaría sin etiqueta. Va al final y con default para no tocar las construcciones existentes.</param>
public sealed record CompanyWithholdingTaxRateDto(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    bool IsActive,
    CompanyMunicipalityDto? City,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <param name="WithholdingTaxRates">Las tasas municipales de la empresa, ordenadas por departamento, municipio y tipo
/// (spec ReteICA por empresa §9.2). Nunca null.</param>
/// <param name="ChargesVat">Si la empresa cobra IVA (spec 2026-11-17 §5.1).</param>
/// <param name="Region">La región (provincia) guardada de una empresa de afuera (spec 2026-11-23); null para Colombia, que
/// la deriva de <c>Department</c>, y para lo que no tiene región. El listado no la trae.</param>
public sealed record CompanyDto(
    Guid Id,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string Country,
    string DocumentType,
    bool IsActive,
    string? Phone,
    string? Email,
    string? Address,
    CompanyCityDto? City,
    CompanyDepartmentDto? Department,
    string? CityName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CompanyWithholdingTaxRateDto> WithholdingTaxRates,
    bool ChargesVat,
    CompanyRegionDto? Region = null);

/// <summary>La región (provincia) de una empresa de afuera, ya resuelta.</summary>
public sealed record CompanyRegionDto(Guid Id, string Name);

public sealed record CompanyResponse(
    Guid Id,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string Country,
    string DocumentType,
    bool IsActive,
    string? Phone,
    string? Email,
    string? Address,
    CompanyCityDto? City,
    CompanyDepartmentDto? Department,
    string? CityName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CompanyWithholdingTaxRateDto> WithholdingTaxRates,
    bool ChargesVat,
    CompanyRegionDto? Region = null);

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
/// <c>City</c> es una sola columna para los dos carriles: el nombre de DIVIPOLA en Colombia y
/// el texto libre fuera de ella, asi la grilla no ramifica (BFF). <c>Country</c> y
/// <c>DocumentType</c> viajan para que la grilla rotule el documento (NIT, NIF, TAX_ID...) sin
/// pedir el detalle.
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
    string Country,
    string DocumentType,
    string? Phone,
    string City,
    bool IsActive,
    bool ChargesVat);

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
// Country y DocumentType son obligatorios. Siguen siendo string? en el cuerpo a proposito: un
// campo ausente tiene que llegar al validador como null y salir como 422 con el mapa errors
// (Country, DocumentType), no morir antes en el binding. La ciudad depende del carril: en
// Colombia es CityId (obligatorio) y fuera de ella es CityName (texto libre, obligatorio).
//
// WithholdingTaxRateIds (spec ReteICA por empresa §9.2) es la excepción al «el PUT reemplaza todo»: ausente o null conserva
// las tasas guardadas (en el alta, ninguna) para que el frontend viejo, que no lo manda, no las borre; [] las quita todas.
//
// ChargesVat (spec 2026-11-17 §5.1) tampoco reemplaza: en el alta, ausente es «cobra IVA»; en el PUT, ausente conserva
// el valor guardado, para que un frontend que todavía no manda el campo no prenda el IVA que otro apagó.
public sealed record CreateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Country,
    string? DocumentType,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    bool? ChargesVat = null,
    Guid? RegionId = null);

public sealed record UpdateCompanyRequest(
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Country,
    string? DocumentType,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    bool? ChargesVat = null,
    Guid? RegionId = null);
