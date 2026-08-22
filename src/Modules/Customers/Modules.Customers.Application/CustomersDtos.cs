namespace Modules.Customers.Application;

public sealed record CustomerDto(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    Guid? PriceListId,
    bool WithRetention,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerResponse(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    Guid? PriceListId,
    bool WithRetention,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// La fila del listado. Es un subconjunto a proposito, igual que en empresas: <c>email</c>,
/// <c>address</c> y <c>department</c> no se pintan en la grilla, y mandarlos multiplica el cuerpo
/// de la respuesta por cada cliente del tenant sin que nadie los mire. Ademas son PII, y el gate
/// del modulo tiene abierta la politica de retencion: cuanto menos PII viaje por una pantalla que
/// no la muestra, menos hay que justificar despues.
///
/// <c>PriceListName</c> viaja **siempre en null**, y no por olvido: el modulo <c>pricing</c> no
/// existe en <c>qep-backend</c>, asi que no hay de donde resolver el nombre de una lista de
/// precios. El campo esta porque el consumidor ya lo declara
/// (<c>CustomerListItemDto</c> en <c>features/customers/types/customer-list.ts</c>) y quitarlo
/// romperia el contrato que ya escribio el frontend; poblarlo exige el modulo que falta.
/// </summary>
public sealed record CustomerListItemResponse(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationNumber,
    string? Phone,
    string? City,
    string? Classification,
    string? PriceListName,
    bool IsActive);

/// <summary>
/// El sobre del listado, con el total que la paginacion necesita.
///
/// Empresas devuelve solo <c>Items</c> porque no pagina; clientes si —el consumidor manda
/// <c>page</c> y <c>pageSize</c>—, y una pagina sin total deja a la UI sin saber cuantas hay.
/// </summary>
public sealed record CustomersResponse(
    IReadOnlyCollection<CustomerListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

// IsActive no viaja en los requests: un cliente nace activo y solo cambia por /deactivate y
// /activate. Un booleano editable convertiria la inactivacion en un PUT comun y la dejaria sin su
// propia entrada de auditoria, el mismo razonamiento que fijo el contrato de producto en CAT-02b y
// el de empresa en EMP-08.
//
// El CUC tampoco viaja: lo emite el backend al crear y no se edita nunca. El formulario tiene el
// campo, pero de solo lectura — `CLI-01` lo dice explicito.
public sealed record CreateCustomerRequest(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    Guid? PriceListId,
    bool WithRetention);

public sealed record UpdateCustomerRequest(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    Guid? PriceListId,
    bool WithRetention);

/// <summary>
/// El acuse de recibo de una importacion.
///
/// **Es un acuse, no un resultado.** `CLI-01` deja el procesamiento del contenido del Excel
/// explicitamente fuera de alcance, y `SDD-OD-10` —el modelo de importacion— sigue abierta. El
/// endpoint responde 202 porque acepto el archivo, no porque haya creado clientes: hoy no crea
/// ninguno.
///
/// <c>Status</c> es <c>"accepted"</c> siempre. Cuando `SDD-OD-10` se cierre y el procesamiento
/// exista, ese campo es el que va a distinguir "en cola" de "procesado" de "fallo", y el
/// consumidor ya lo lee.
/// </summary>
public sealed record ImportCustomersResponse(
    string FileName,
    DateTimeOffset ReceivedAt,
    string Status);

// El catalogo de clasificaciones de cliente: nombre + prefijo, mismo shape que TaxRateDto en
// Catalog. IsActive no viaja en los requests, mismo criterio que Customer: nace activa y solo
// cambia por /deactivate y /activate.
public sealed record ClientClassificationDto(
    Guid Id,
    string Name,
    string Prefix,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ClientClassificationResponse(
    Guid Id,
    string Name,
    string Prefix,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ClientClassificationsResponse(
    IReadOnlyCollection<ClientClassificationResponse> Items);

public sealed record CreateClientClassificationRequest(string Name, string Prefix);

public sealed record UpdateClientClassificationRequest(string Name, string Prefix);
