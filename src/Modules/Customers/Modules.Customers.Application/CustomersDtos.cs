namespace Modules.Customers.Application;

/// <summary>
/// La ciudad de un cliente, resuelta. Tipo propio de <c>Customers</c> y no el <c>CityDto</c> de
/// <c>Modules.Geography.Application</c> aunque el shape coincida: este modulo no puede referenciar
/// ese ensamblado (ver <see cref="ICustomerGeographyLookup"/>), asi que reusarlo no es una opcion.
/// </summary>
public sealed record CustomerCityDto(Guid Id, string DivipolaCode, string Name);

/// <summary>La contraparte de <see cref="CustomerCityDto"/> para el departamento.</summary>
public sealed record CustomerDepartmentDto(Guid Id, string DivipolaCode, string Name);

public sealed record CustomerDto(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    CustomerCityDto City,
    CustomerDepartmentDto Department,
    ClientClassificationDto Classification,
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
    CustomerCityDto City,
    CustomerDepartmentDto Department,
    ClientClassificationDto Classification,
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
/// <c>City</c> y <c>Classification</c> si viajan, resueltos, con el mismo criterio liviano:
/// <c>Department</c> se omite aca aunque el detalle lo lleve, porque la grilla ya pinta la ciudad
/// y el departamento es redundante para esa vista.
///
/// Sin lista de precios: a diferencia de la clasificacion (1:1), un cliente puede tener varias a
/// la vez, asi que no hay un solo nombre que mostrar en esta fila liviana. Quien necesite ver las
/// listas asignadas las pide por separado
/// (<c>GET /customers/{customerId}/price-lists</c>).
/// </summary>
public sealed record CustomerListItemResponse(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationNumber,
    string? Phone,
    CustomerCityDto City,
    ClientClassificationDto Classification,
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
//
// CityId y ClassificationId son obligatorios: la Fase 3 hizo la ciudad y la clasificacion FKs de
// primer nivel, ya no texto libre opcional.
public sealed record CreateCustomerRequest(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention);

public sealed record UpdateCustomerRequest(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention);

/// <summary>
/// Una fila que se importo con exito: su numero de fila en el Excel (2-based; la fila 1 es la
/// cabecera), el CUC que le emitio el backend y su nombre, para que quien subio el archivo pueda
/// ubicarla sin tener que volver a abrirlo.
/// </summary>
public sealed record ImportedCustomerRow(int RowNumber, string Cuc, string Name);

/// <summary>
/// Una fila que NO se importo, con lo que la persona que subio el archivo necesita para
/// corregirla: en que fila esta, un codigo de error estable (<c>customers.import.row.*</c>) y el
/// mensaje. <c>Field</c> viaja solo cuando el error es de un campo puntual (una celda vacia, un
/// formato invalido); para errores del par Departamento+Ciudad o de duplicado va <c>null</c>,
/// porque no son culpa de una sola columna.
/// </summary>
public sealed record ImportRowError(int RowNumber, string Code, string Message, string? Field);

/// <summary>
/// El resultado de una importacion. A diferencia del acuse original (`CLI-01` dejaba el
/// procesamiento del Excel fuera de alcance y `SDD-OD-10` seguia abierta), esta es la Fase 5: el
/// archivo se procesa de verdad, fila por fila, y el cuerpo lleva el detalle completo — ninguna
/// fila valida se pierde porque otra del mismo archivo tenga un error.
///
/// <c>Status</c> reemplaza al <c>"accepted"</c> fijo original con tres valores utiles para la UI:
/// <c>"completed"</c> (todo se importo), <c>"completed_with_errors"</c> (una mezcla) y
/// <c>"failed"</c> (el archivo era valido estructuralmente pero ninguna fila lo era). El endpoint
/// devuelve 202 en los tres casos: el archivo en si se proceso, que es lo que 202 promete. Un
/// archivo estructuralmente invalido (columnas faltantes, sin filas de datos) nunca llega a armar
/// esta respuesta — sale como 422 antes.
/// </summary>
public sealed record ImportCustomersResponse(
    string FileName,
    DateTimeOffset ReceivedAt,
    string Status,
    int TotalRows,
    int ImportedCount,
    int ErrorCount,
    IReadOnlyList<ImportedCustomerRow> Imported,
    IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// El archivo <c>.xlsx</c> de la plantilla de importacion (Fase 6), ya generado y listo para
/// devolver como el cuerpo de la respuesta HTTP.
/// </summary>
public sealed record CustomerImportTemplateFile(byte[] Content, string FileName);

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
