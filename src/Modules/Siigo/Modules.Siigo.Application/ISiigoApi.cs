namespace Modules.Siigo.Application;

/// <summary>
/// Las credenciales de una conexión de Siigo, ya descifradas por el puerto de conexiones. Nunca se imprimen: el <c>ToString</c> no
/// escribe la clave de acceso (un <c>record</c> la escribiría), así que un log o una excepción con el objeto no la filtran.
/// </summary>
public sealed record SiigoCredentials(Guid ConnectionId, string Username, string AccessKey)
{
    public override string ToString() => $"SiigoCredentials {{ ConnectionId = {ConnectionId} }}";
}

/// <summary>Una opción de una lista de Siigo (comprobante, centro de costo, bodega, forma de pago).</summary>
public sealed record SiigoOption(string Id, string Name, string? Description = null);

/// <summary>Un usuario activo de Siigo. <paramref name="Email"/> es con lo que se cruza el asesor del pedido para elegir el vendedor de
/// la factura (spec 2026-11-20).</summary>
public sealed record SiigoUser(string Id, string Name, string? Email);

/// <summary>Un impuesto de <c>GET /v1/taxes</c>. <paramref name="Type"/> llega como <c>IVA</c>, <c>Retefuente</c>, <c>ReteICA</c>…; el
/// porcentaje de ReteICA viene por mil.</summary>
public sealed record SiigoTaxOption(string Id, string Name, string Type, decimal Percentage, bool Active);

/// <summary>Un producto de <c>GET /v1/products</c>. Sólo uno <paramref name="Active"/> se puede facturar.</summary>
public sealed record SiigoProductOption(string Id, string Code, string Name, bool Active);

/// <summary>Una página de un listado de Siigo.</summary>
public sealed record SiigoPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalResults);

/// <summary>El cliente de Siigo que ya existe y está activo: con su id y su identificación se factura.</summary>
public sealed record SiigoCustomerRef(string Id, string Identification);

/// <summary>
/// El cliente que se crea en Siigo antes de facturar (spec 2026-11-18 §6.2): <c>type: "Customer"</c>, <c>person_type</c>
/// (<c>Person</c> | <c>Company</c>), <c>id_type</c> como texto, <c>name</c> como lista (<c>[nombres, apellidos]</c> o <c>[razón social]</c>),
/// <c>active: true</c>, dirección con los códigos de país, departamento y ciudad (DANE), teléfono y contacto. Lo arma el armador de la
/// factura; el cliente HTTP sólo lo serializa.
/// </summary>
public sealed record SiigoCustomerRequest(
    string PersonType,
    string IdTypeCode,
    string Identification,
    IReadOnlyList<string> Name,
    string? Address,
    string CountryCode,
    string StateCode,
    string CityCode,
    SiigoPhone? Phone,
    string? ContactFirstName,
    string? ContactLastName,
    string? ContactEmail)
{
    // Un record imprimiría documento, nombre, dirección y contacto del cliente en cualquier log o excepción que lo interpole.
    public override string ToString() => "SiigoCustomerRequest { }";
}

/// <summary>
/// El cuerpo de <c>POST /v1/invoices</c>, ya serializado. Va como texto y no como objeto a propósito: el armador lo guarda en
/// <c>request_json</c> y un reintento manda <b>los mismos bytes</b> con la misma <c>Idempotency-Key</c>; volver a serializar un objeto
/// podría cambiar un byte y, con él, la idempotencia (spec 2026-11-18 §5.4, paso 1). Lleva datos del cliente: nunca sale a un log.
/// </summary>
public sealed record SiigoInvoiceRequest(string Json)
{
    public override string ToString() => "SiigoInvoiceRequest { }";
}

/// <summary>Un error de Siigo: <c>{ "Errors": [ { "Code", "Message" } ] }</c>. El mensaje es de Siigo y puede traer valores del pedido,
/// no del cliente.</summary>
public sealed record SiigoError(string Code, string Message);

/// <summary>El resultado de <c>POST /v1/invoices</c>. Una unión cerrada: <see cref="Created"/>, <see cref="Rejected"/> (Siigo la
/// rechazó y no la creó) o <see cref="Transient"/> (no se sabe: hay que reintentar con la misma clave).</summary>
public abstract record SiigoInvoiceOutcome
{
    private SiigoInvoiceOutcome()
    {
    }

    /// <summary>Siigo la creó. <paramref name="Cufe"/> sólo viene si ya salió a la DIAN.</summary>
    public sealed record Created(string Id, string Name, decimal Total, string? Cufe) : SiigoInvoiceOutcome;

    /// <summary>Siigo respondió un error de validación (o rechazó las credenciales). <paramref name="ExpectedPaymentsTotal"/> sale del
    /// último número del mensaje de <c>invalid_total_payments</c>, leído con cultura invariante; <c>null</c> si no se puede leer.</summary>
    public sealed record Rejected(IReadOnlyList<SiigoError> Errors, decimal? ExpectedPaymentsTotal) : SiigoInvoiceOutcome;

    /// <summary>429, 5xx, timeout, red o una respuesta ilegible: no se sabe si Siigo la creó.</summary>
    public sealed record Transient(string Reason) : SiigoInvoiceOutcome;
}

/// <summary>El resultado de <c>POST /v1/invoices/{id}/stamp</c>.</summary>
public abstract record SiigoStampOutcome
{
    private SiigoStampOutcome()
    {
    }

    public sealed record Stamped(string? Cufe) : SiigoStampOutcome;

    public sealed record Rejected(IReadOnlyList<SiigoError> Errors) : SiigoStampOutcome;

    public sealed record Transient(string Reason) : SiigoStampOutcome;
}

/// <summary>El resultado de <c>GET /v1/invoices/{id}</c> (spec 2026-11-25, anulación §2). Una unión cerrada: <see cref="Found"/>,
/// <see cref="NotFound"/> (Siigo dijo sin ambigüedad que no existe) o <see cref="Unrecognized"/> (no se entiende: quien llama no libera
/// nada).</summary>
public abstract record SiigoInvoiceLookup
{
    private SiigoInvoiceLookup()
    {
    }

    /// <summary>Siigo devolvió esa factura. <paramref name="StampStatus"/> es <c>stamp.status</c> (<c>Draft</c>, <c>Accepted</c>,
    /// <c>Rejected</c>), si vino.</summary>
    public sealed record Found(bool Annulled, string? StampStatus) : SiigoInvoiceLookup;

    /// <summary>Un 404 cuyos errores están todos en <see cref="SiigoApiErrors.InvoiceNotFoundCodes"/>.</summary>
    public sealed record NotFound : SiigoInvoiceLookup;

    /// <summary>Cualquier otra respuesta: otro 4xx, o un 2xx sin <c>annulled</c> booleano, de otra factura o ilegible.</summary>
    public sealed record Unrecognized(int? Status) : SiigoInvoiceLookup;
}

/// <summary>
/// El puerto de la API de Siigo (spec 2026-11-18 §3, §5.4). Todas las llamadas reciben las credenciales de la conexión. Las lecturas
/// (listas, productos, clientes) lanzan: <c>ServiceUnavailableException</c> <c>siigo.unavailable</c> si Siigo no contestó o dio un
/// error transitorio, y <c>ResourceConflictException</c> <c>siigo.connection_unavailable</c> si rechazó las credenciales. La creación
/// de facturas no lanza por eso: devuelve <see cref="SiigoInvoiceOutcome"/>, porque «no sé si se creó» es un resultado que el flujo
/// tiene que guardar, no una excepción. Sin reintentos propios: reintenta quien llama, con la misma fila y la misma clave.
/// </summary>
public interface ISiigoApi
{
    /// <summary><c>GET /v1/document-types?type=FV</c>.</summary>
    Task<IReadOnlyList<SiigoOption>> ListDocumentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/users</c>, sólo los activos y de todas las páginas. Cacheada como las demás listas; con
    /// <paramref name="refresh"/> se vuelve a leer de Siigo y la caché queda con lo nuevo.</summary>
    Task<IReadOnlyList<SiigoUser>> ListUsersAsync(SiigoCredentials credentials, bool refresh, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/cost-centers</c>.</summary>
    Task<IReadOnlyList<SiigoOption>> ListCostCentersAsync(SiigoCredentials credentials, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/warehouses</c>.</summary>
    Task<IReadOnlyList<SiigoOption>> ListWarehousesAsync(SiigoCredentials credentials, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/payment-types?document_type=FV</c>.</summary>
    Task<IReadOnlyList<SiigoOption>> ListPaymentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/taxes</c>.</summary>
    Task<IReadOnlyList<SiigoTaxOption>> ListTaxesAsync(SiigoCredentials credentials, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/products</c>, de a <see cref="SiigoApiErrors.ProductsPageSize"/>, con búsqueda opcional por código. Sin caché:
    /// el auto-vínculo recorre todas las páginas.</summary>
    Task<SiigoPage<SiigoProductOption>> SearchProductsAsync(
        SiigoCredentials credentials, string? code, int page, CancellationToken cancellationToken);

    /// <summary>Todos los productos <b>activos</b> de la conexión (recorre cada página de <c>GET /v1/products</c>), para el selector del
    /// paso 6. Se guardan 60 s por conexión, sólo para que escribir y paginar no recorra Siigo en cada tecla; <paramref name="refresh"/>
    /// se salta la copia y la repuebla, para que un producto recién creado en Siigo aparezca al abrir el selector.</summary>
    Task<IReadOnlyList<SiigoProductOption>> ListActiveProductsAsync(
        SiigoCredentials credentials, bool refresh, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/customers?identification=</c>: el cliente activo, o <c>null</c>. Nunca se cachea.</summary>
    Task<SiigoCustomerRef?> FindCustomerAsync(SiigoCredentials credentials, string identification, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/customers</c>. Nunca se cachea.</summary>
    Task<SiigoCustomerRef> CreateCustomerAsync(
        SiigoCredentials credentials, SiigoCustomerRequest request, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/invoices</c> con <c>Idempotency-Key</c> (alfanumérica, hasta 30 caracteres; otra es un error de programación
    /// y lanza <see cref="ArgumentException"/> sin llamar a Siigo). Un 401 reautentica una vez y repite con la misma clave.</summary>
    Task<SiigoInvoiceOutcome> CreateInvoiceAsync(
        SiigoCredentials credentials, SiigoInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/invoices/{id}/stamp</c>: envía a la DIAN una factura ya creada.</summary>
    Task<SiigoStampOutcome> StampInvoiceAsync(SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/invoices/{id}</c>: si la factura está anulada. Lanza como las demás lecturas (503, 502 de servicio, 409 de
    /// credenciales); cualquier otra respuesta que no se entienda es <see cref="SiigoInvoiceLookup.Unrecognized"/> y queda en
    /// <c>call_failures</c>. El cuerpo trae datos del cliente: no sale de aquí. Nunca se cachea.</summary>
    Task<SiigoInvoiceLookup> GetInvoiceAsync(SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/invoices/{id}/pdf</c>: el PDF de la factura, ya decodificado (Siigo lo manda en base64 dentro de un JSON). Lanza
    /// como las demás lecturas, y <c>siigo.unavailable</c> si Siigo contesta sin contenido. Nunca se cachea: pesa y es de un solo uso.</summary>
    Task<byte[]> GetInvoicePdfAsync(SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/invoices/{id}/stamp/errors</c>: el rechazo de la DIAN.</summary>
    Task<IReadOnlyList<SiigoError>> GetStampErrorsAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken);
}
