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
    string? Cuc,
    /// <summary>El nombre de la persona de contacto.</summary>
    string Name,
    /// <summary>La razon social, cuando el cliente es una empresa. Null si no lo es.</summary>
    string? BusinessName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    /// <summary>La calle del **domicilio del cliente**; su ciudad va en <c>City</c>. Se conserva
    /// plano —y no solo dentro de `Addresses`— porque es lo que la cotizacion y el PDF muestran
    /// como domicilio. Las direcciones de envio van en `Addresses`, y marcar otra como principal
    /// no cambia este campo (spec 2026-09-18).</summary>
    string? Address,
    /// <summary>El pais del cliente, ISO-3166-1 alpha-2 (<c>CO</c>, <c>ES</c>). Decide cual de los
    /// dos carriles de ciudad viene lleno.</summary>
    string? Country,
    /// <summary>La ciudad DIVIPOLA resuelta. **Null cuando el cliente no es de Colombia**: su
    /// ciudad viaja en <c>CityName</c>.</summary>
    CustomerCityDto? City,
    /// <summary>El departamento DIVIPOLA resuelto. Null por lo mismo que <c>City</c>.</summary>
    CustomerDepartmentDto? Department,
    /// <summary>La ciudad escrita a mano de un cliente de afuera. Null para uno colombiano, que
    /// usa <c>City</c>. Quien pinta la ficha usa <c>City?.Name ?? CityName</c>.</summary>
    string? CityName,
    ClientClassificationDto? Classification,
    IReadOnlyCollection<CustomerAddressDto> Addresses,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    /// <summary>Spec 2026-10-10 §5.2: con <c>false</c>, CUC, documento, correo, dirección, país, ciudad y clasificación pueden venir en <c>null</c>.</summary>
    bool IsComplete,
    /// <summary>Spec 2026-10-11 §4.2: las etiquetas del cliente, por nombre. Viaja siempre, vacía si no tiene.</summary>
    IReadOnlyCollection<TagDto> Tags,
    /// <summary>Spec 2026-10-11 decisión 10. Viaja siempre; con <c>optedOut = false</c> sus otros campos son null.</summary>
    MarketingConsentDto MarketingConsent,
    /// <summary>Spec 2026-10-14 decisión 10: el <c>@usuario</c> de Instagram de un cliente que escribió por ese canal; null si no.</summary>
    string? InstagramUsername = null);

public sealed record CustomerResponse(
    Guid Id,
    string? Cuc,
    string Name,
    string? BusinessName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Country,
    CustomerCityDto? City,
    CustomerDepartmentDto? Department,
    string? CityName,
    ClientClassificationDto? Classification,
    IReadOnlyCollection<CustomerAddressDto> Addresses,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    /// <summary>Spec 2026-10-10 §5.2: con <c>false</c>, CUC, documento, correo, dirección, país, ciudad y clasificación pueden venir en <c>null</c>.</summary>
    bool IsComplete,
    IReadOnlyCollection<TagDto> Tags,
    MarketingConsentDto MarketingConsent);

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
    string? Cuc,
    string Name,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Country,
    CustomerCityDto? City,
    CustomerDepartmentDto? Department,
    string? CityName,
    ClientClassificationDto? Classification,
    bool IsActive,
    /// <summary>Spec 2026-10-10 §5.2: con <c>false</c>, CUC, documento, correo, dirección, país, ciudad y clasificación pueden venir en <c>null</c>.</summary>
    bool IsComplete);

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
// ClassificationId es opcional (decision del owner, 2026-10-19): el formulario de alta la ofrece
// primero pero no la exige, y sin ella el CUC no lleva prefijo. Ausente o null es "sin
// clasificacion"; en un PUT eso la quita. CityId era obligatorio hasta que llego el pais: ahora es obligatorio **solo
// para Colombia**, y un cliente de afuera manda CityName en su lugar. Cual de los dos hace falta
// lo decide CustomerWriteRules segun Country, y el dominio lo vuelve a exigir en
// Customer.EnsureValidLocation.
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
    /// <summary>ISO-3166-1 alpha-2. Obligatorio.</summary>
    string Country,
    /// <summary>La ciudad DIVIPOLA. Obligatoria si <c>Country</c> es <c>CO</c>, ignorada si
    /// no.</summary>
    Guid? CityId,
    /// <summary>La ciudad escrita a mano. Obligatoria si <c>Country</c> **no** es <c>CO</c>,
    /// ignorada si lo es.</summary>
    string? CityName,
    Guid? ClassificationId,
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
    string Country,
    Guid? CityId,
    string? CityName,
    Guid? ClassificationId,
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

/// <summary>
/// El dueño de un documento repetido, en el <c>existing</c> del 422 <c>customers.customer.identification_taken</c>. Tiene la
/// forma de <c>PosCustomerOptionResponse</c> más <c>Cuc</c>: la pantalla ofrece ir al cliente que ya tiene el documento y,
/// si <c>IsActive</c> es false, explica por qué no se puede usar. <c>Cuc</c> es <c>null</c> en un cliente incompleto (sin
/// CUC aún) y <c>IsComplete</c> dice si falta completarlo; sin esos dos campos la pantalla tendría que pedir la ficha.
/// </summary>
public sealed record CustomerIdentificationOwnerDto(
    Guid Id,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Cuc,
    bool IsComplete,
    bool IsActive);

/// <summary>
/// Una fila del aviso «este documento ya existe». BFF (§3.2): <c>Match</c> lo calcula el servidor para que la pantalla elija
/// el color por un campo y no comparando tipos ni formatos de número; el resto tiene la forma del dueño de un 422 de documento
/// repetido, que es lo que el formulario ya sabe dibujar.
/// </summary>
public sealed record CustomerIdentificationMatchDto(
    Guid Id,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Cuc,
    bool IsComplete,
    bool IsActive,
    string Match);

/// <summary>Siempre <c>items</c>, vacía sin coincidencias; sin total: es un aviso, no un listado.</summary>
public sealed record CustomerIdentificationMatchesResponse(IReadOnlyList<CustomerIdentificationMatchDto> Items);

/// <summary>
/// Spec 2026-10-24 §3.3: una clase de anomalía y cuántos grupos tiene. <c>counts</c> viaja con las dos clases siempre, también
/// en cero: las pestañas dibujan su contador sin conocer el enum del backend, y una clase que desaparece de la lista obligaría
/// a la pantalla a saber cuál falta.
/// </summary>
public sealed record CustomerAnomalyCountDto(string Kind, int Groups);

/// <summary>
/// Un cliente dentro de un grupo de anomalías. Trae <c>phone</c> tal cual se escribió y <c>isComplete</c>/<c>isActive</c> como
/// campos para que la pantalla marque al incompleto o al inactivo sin comparar estados; el documento y el CUC son
/// <c>null</c> en un cliente de WhatsApp sin ficha.
/// </summary>
public sealed record CustomerAnomalyCustomerDto(
    Guid Id,
    string Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Cuc,
    string? Phone,
    bool IsComplete,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// Un grupo de clientes con la misma <c>key</c> (el celular en E.164, o el número de documento normalizado), del más viejo al
/// más nuevo. <c>truncated</c> existe siempre: un grupo de más de 50 se corta y la pantalla lo dice sin contar ni conocer el
/// tope.
/// </summary>
public sealed record CustomerAnomalyGroupDto(string Key, IReadOnlyList<CustomerAnomalyCustomerDto> Customers, bool Truncated);

/// <summary>
/// Pagina grupos, no clientes, y <c>total</c> son los grupos de la clase pedida: la pantalla pagina lo que dibuja. <c>counts</c>
/// lleva las dos clases para los contadores de las pestañas.
/// </summary>
public sealed record CustomerAnomaliesResponse(
    IReadOnlyList<CustomerAnomalyCountDto> Counts,
    IReadOnlyList<CustomerAnomalyGroupDto> Groups,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Una de las partes de la fusión, tal como la confirmación la dibuja: quién es, con qué documento y si se puede
/// fusionar. BFF: <c>Version</c> es la de <c>Customer</c> —un entero— y es el <c>If-Match</c> que el POST de la fusión
/// manda (B1), así el cliente no tiene que pedir la ficha aparte para conseguirla. <c>IdentificationType</c>,
/// <c>IdentificationNumber</c> y <c>Cuc</c> son <c>null</c> en una ficha incompleta; <c>IsComplete</c> e <c>IsActive</c>
/// dejan marcar en la pantalla qué fila no podría quedar.
/// </summary>
public sealed record CustomerMergePartyDto(
    Guid Id,
    string Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Cuc,
    bool IsComplete,
    bool IsActive,
    long Version);

/// <summary>Una clase de lo que la fusión mueve al que queda, con cuántas hay. <c>Kind</c> viaja con el nombre del enum.</summary>
public sealed record CustomerMergeMoveDto(string Kind, int Count);

/// <summary>
/// La vista previa de una fusión (§3.4). BFF: <c>Moves</c> viaja con las ocho clases siempre, en el orden del enum y también
/// en cero, para que la confirmación dibuje en gris la que está en cero sin conocer el enum; <c>Tags</c> son las que el que
/// queda gana de verdad (sin repetir y dentro del tope), no las que traen los fusionados. <c>FilledFields</c> son nombres
/// de <c>CustomerMergeField</c> (B2): lo que la ficha del que queda recibe. <c>DocumentsDiffer</c> avisa que algún
/// fusionado tiene un número distinto del que queda al normalizar ambos; si a uno de los dos le falta el número, no cuenta.
/// </summary>
public sealed record CustomerMergePreviewResponse(
    CustomerMergePartyDto Survivor,
    IReadOnlyList<CustomerMergePartyDto> Merged,
    bool DocumentsDiffer,
    IReadOnlyList<CustomerMergeMoveDto> Moves,
    IReadOnlyList<string> FilledFields);

/// <summary>D-F5: una etiqueta que no cupo en el que queda. Sólo lo que el aviso nombra: el color y el conteo no hacen falta
/// para decir cuáles no cupieron.</summary>
public sealed record CustomerMergeDroppedTagDto(Guid Id, string Name);

/// <summary>El cuerpo de <c>POST /customers/{survivorId}/merge</c>. Opcional en el binding para que una lista ausente sea un
/// 422 con <c>errors.MergedCustomerIds</c> del validador y no un 400 del deserializador.</summary>
public sealed record MergeCustomersRequest(IReadOnlyList<Guid>? MergedCustomerIds);

/// <summary>
/// La respuesta de la fusión (B3). BFF: los mismos campos que <see cref="CustomerResponse"/>, en el mismo objeto, para que
/// la pantalla repinte la ficha del que quedó con el mismo código que dibuja un <c>GET</c>, más <c>DroppedTags</c>, que viaja
/// siempre (<c>[]</c> si todo cupo) para que el aviso no tenga que distinguir «vacío» de «ausente». Sin versión: al terminar,
/// el frontend navega a la ficha del que quedó, y una fusión nueva pide otra vista previa, que es de donde sale el
/// <c>If-Match</c>.
/// </summary>
public sealed record CustomerMergeResponse(
    Guid Id,
    string? Cuc,
    string Name,
    string? BusinessName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Country,
    CustomerCityDto? City,
    CustomerDepartmentDto? Department,
    string? CityName,
    ClientClassificationDto? Classification,
    IReadOnlyCollection<CustomerAddressDto> Addresses,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsComplete,
    IReadOnlyCollection<TagDto> Tags,
    MarketingConsentDto MarketingConsent,
    IReadOnlyList<CustomerMergeDroppedTagDto> DroppedTags);
