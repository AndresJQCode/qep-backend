namespace Modules.Customers.Application;

/// <summary>
/// La ciudad de un cliente, resuelta. Tipo propio de <c>Customers</c> y no el <c>CityDto</c> de
/// <c>Modules.Geography.Application</c> aunque el shape coincida: este modulo no puede referenciar
/// ese ensamblado (ver <see cref="ICustomerGeographyLookup"/>), asi que reusarlo no es una opcion.
/// </summary>
public sealed record CustomerCityDto(Guid Id, string DivipolaCode, string Name);

/// <summary>La contraparte de <see cref="CustomerCityDto"/> para el departamento.</summary>
public sealed record CustomerDepartmentDto(Guid Id, string DivipolaCode, string Name);


/// <summary>Una direccion del cliente. `Address` es la calle; `Name` es a quien pertenece
/// ("Bodega Norte"). El departamento no viaja: es el de la ciudad, y el frontend ya lo resuelve
/// contra `geography` para filtrar el combobox.</summary>
public sealed record CustomerAddressDto(
    Guid Id,
    string Name,
    string Address,
    string? Phone,
    Guid CityId,
    string CityName,
    /// <summary>El departamento **no** se guarda —es el de la ciudad— pero sí viaja: el
    /// formulario lo necesita para filtrar el combobox de ciudad al editar la dirección.</summary>
    Guid DepartmentId,
    string DepartmentName,
    bool IsPrincipal);

public sealed record CustomerAddressRequest(
    string Name,
    string Address,
    Guid CityId,
    string? Phone,
    bool IsPrincipal);

public sealed record CustomerDto(
    Guid Id,
    string Cuc,
    /// <summary>El nombre de la persona de contacto.</summary>
    string Name,
    /// <summary>La razon social, cuando el cliente es una empresa. Null si no lo es.</summary>
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    /// <summary>La calle de la direccion **principal**. Se conserva plano —y no solo dentro de
    /// `Addresses`— porque es lo que la cotizacion y el PDF muestran como domicilio del
    /// cliente.</summary>
    string? Address,
    CustomerCityDto City,
    CustomerDepartmentDto Department,
    ClientClassificationDto Classification,
    IReadOnlyCollection<CustomerAddressDto> Addresses,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerResponse(
    Guid Id,
    string Cuc,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    CustomerCityDto City,
    CustomerDepartmentDto Department,
    ClientClassificationDto Classification,
    IReadOnlyCollection<CustomerAddressDto> Addresses,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// La fila del listado. Es un subconjunto a proposito, igual que en empresas: <c>address</c> no
/// se pinta en la grilla, y mandarla multiplica el cuerpo de la respuesta por cada cliente del
/// tenant sin que nadie la mire.
///
/// <c>Email</c> si viaja (CLI-FILTROS-01, columna "Contacto" junto a <c>Phone</c>) — decision de
/// producto explicita de mostrar PII de contacto en la grilla, a cambio de que el usuario no
/// tenga que abrir el detalle para ver como comunicarse con el cliente.
///
/// <c>City</c>, <c>Department</c> y <c>Classification</c> viajan resueltos. <c>Department</c> se
/// agrego para el filtro multiple de Departamento/Ciudad del listado: sin el, elegir un
/// departamento en el filtro no tiene con que columna mostrarse en la grilla.
/// </summary>
public sealed record CustomerListItemResponse(
    Guid Id,
    string Cuc,
    string Name,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    CustomerCityDto City,
    CustomerDepartmentDto Department,
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
// El CUC tampoco viaja: lo emite el backend al crear. El formulario tiene el campo, pero de solo
// lectura — `CLI-01` lo dice explicito; su prefijo puede cambiar en un Update, pero por el lado
// del servidor, al resolver la clasificacion, nunca porque el cliente lo haya mandado.
//
// CityId y ClassificationId son obligatorios: la Fase 3 hizo la ciudad y la clasificacion FKs de
// primer nivel, ya no texto libre opcional.
public sealed record CreateCustomerRequest(
    string Name,
    /// <summary>Opcional: solo los clientes que son empresas la tienen. Vacio y ausente son lo
    /// mismo.</summary>
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention,
    bool VatSurplus);

public sealed record UpdateCustomerRequest(
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid CityId,
    Guid ClassificationId,
    bool WithRetention,
    bool VatSurplus);

/// <summary>
/// Una fila que se importo con exito: su numero de fila en el Excel (2-based; la fila 1 es la
/// cabecera), el CUC que le emitio el backend y su nombre, para que quien subio el archivo pueda
/// ubicarla sin tener que volver a abrirlo.
/// </summary>
/// <summary>
/// <c>Action</c> es <c>"created"</c> o <c>"updated"</c> — mismo estilo de string-enum que
/// <see cref="ImportCustomersResponse.Status"/>, para no agregar un segundo shape de respuesta
/// solo para distinguir los dos casos que <c>ImportCustomersHandler</c> ahora produce.
/// </summary>
public sealed record ImportedCustomerRow(int RowNumber, string Cuc, string Name, string Action);

/// <summary>
/// Los campos crudos de una fila del Excel, texto tal cual la persona lo tipeo (recortado,
/// vacio-a-null) — sin importar si esa fila termino siendo valida. Viaja en cada
/// <see cref="ImportRowError"/> para que el modal de errores del frontend pueda ofrecer
/// descargar un Excel ya cargado sólo con las filas que fallaron (<c>ExportFailedCustomerRows</c>)
/// — la persona corrige las celdas marcadas y reimporta ese archivo más chico, en vez del
/// original completo. El modal no edita estos datos in situ: sólo los reenvía tal cual al pedir
/// la descarga.
/// </summary>
public sealed record CustomerImportRowData(
    string? Cuc,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string Department,
    string City,
    string Classification,
    string? WithRetention,
    string? VatSurplus);

/// <summary>
/// Una fila que NO se importo, con lo que la persona que subio el archivo necesita para
/// corregirla: en que fila esta, un codigo de error estable (<c>customers.import.row.*</c>) y el
/// mensaje. <c>Field</c> viaja solo cuando el error es de un campo puntual (una celda vacia, un
/// formato invalido); para errores del par Departamento+Ciudad o de duplicado va <c>null</c>,
/// porque no son culpa de una sola columna.
///
/// <c>RowData</c> viaja siempre — ver <see cref="CustomerImportRowData"/>.
/// </summary>
public sealed record ImportRowError(
    int RowNumber, string Code, string Message, string? Field, CustomerImportRowData? RowData = null);

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

/// <summary>
/// El cuerpo de <c>POST .../customers/import/failed-rows</c>: las filas que el frontend ya
/// recibió como <see cref="ImportRowError.RowData"/> de una importación anterior, tal cual, para
/// devolverlas armadas en un Excel nuevo. El frontend nunca las edita — sólo las reenvía.
/// </summary>
public sealed record ExportFailedCustomerRowsRequest(IReadOnlyList<CustomerImportRowData> Rows);

/// <summary>
/// La respuesta de <c>POST .../customers/export</c>. Confirma que el archivo se genero y se subio,
/// y hasta cuando va a servir el enlace — el enlace en si sale por correo, no por acá.
/// </summary>
public sealed record ExportCustomersResponse(
    string FileName,
    int CustomerCount,
    DateTimeOffset ExpiresAt);

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
