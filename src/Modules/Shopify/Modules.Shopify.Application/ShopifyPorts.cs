using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.2: la conexión de Integrations que corresponde a una tienda, por su dominio (<c>connection_routes</c>). Puerto
/// del módulo con adaptador en Bootstrapper: Shopify no referencia Integrations (ShopifyLayerTests).
/// </summary>
public interface IShopifyConnectionDirectory
{
    /// <summary><c>null</c> si ninguna ruta apunta a esa tienda: el webhook se cierra como <c>unknown_shop</c>. El dominio se
    /// compara recortado y en minúsculas. Una conexión <c>Paused</c> o <c>NeedsAttention</c> también vuelve, con
    /// <see cref="ShopifyConnectionRef.IsOperational"/> en <c>false</c>: sin eso, un <c>app/uninstalled</c> o un pedido de una
    /// tienda pausada se perderían como <c>unknown_shop</c>.</summary>
    Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken);

    /// <summary>§5.3: las conexiones operativas de todos los tenants, para la conciliación. Cross-tenant a propósito, como la
    /// ruta del webhook: el cron no corre en nombre de ningún tenant.</summary>
    Task<IReadOnlyList<ShopifyConnectionRef>> ListOperationalAsync(CancellationToken cancellationToken);

    /// <summary>§5.3 «Sincronizar ahora»: la conexión de Shopify <paramref name="connectionId"/> del tenant, en cualquier estado
    /// (con <see cref="ShopifyConnectionRef.IsOperational"/> en <c>false</c> si no lo es); <c>null</c> si no existe en ese tenant
    /// o no es de Shopify. El handler la responde 403, nunca 404.</summary>
    Task<ShopifyConnectionRef?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Lo que el módulo sabe de una conexión. <see cref="IsOperational"/> es conexión <c>Active</c> <b>y</b> módulo <c>shopify</c>
/// activo en el tenant: sin eso el espejo se actualiza igual y no se importa (§5.2). <see cref="AccessToken"/> sólo viaja si es
/// operativa y nunca se imprime: <see cref="ToString"/> lo omite, para que un log del registro no lo filtre (§12).
/// </summary>
public sealed record ShopifyConnectionRef(
    Guid TenantId, Guid ConnectionId, string ShopId, string ShopDomain, bool IsOperational, string? AccessToken)
{
    /// <summary>D-S4 y §5.3: el instante del enlace (la creación de la conexión). La tienda que el módulo ve por
    /// primera vez arranca ahí su ventana de conciliación, para que un pedido hecho entre el enlace y el primer contacto entre
    /// aunque su webhook se pierda. Nulo si el adaptador no lo sabe: entonces, «ahora».</summary>
    public DateTimeOffset? LinkedAt { get; init; }

    public override string ToString() =>
        $"ShopifyConnectionRef {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, ShopId = {ShopId}, ShopDomain = {ShopDomain}, IsOperational = {IsOperational} }}";
}

/// <summary>Spec 2026-10-15 §5.2 y §5.3: avisarle a Integrations que la tienda dejó de responder. Adaptador en Bootstrapper sobre
/// <c>IConnectionHealthReporter</c>: <see cref="ReportUninstalledAsync"/> con <c>app_uninstalled</c>,
/// <see cref="ReportRejectedAsync"/> con <c>credentials_rejected</c>.</summary>
public interface IShopifyConnectionHealth
{
    Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task ReportRejectedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-15 §6.2: el intento de importar una fila en espera. Lo implementa <see cref="ShopifyOrderImporter"/>;
/// acá está el contrato para que el webhook, la conciliación y el reintento manual lo llamen igual. Evalúa y escribe la fila; el
/// commit final es del llamador. La única escritura que confirma por su cuenta es el cliente anotado en la fila antes del pedido
/// (D-S24).
/// <para><paramref name="connection"/> va porque la clave externa del pedido es <c>"{shopId}:{shopifyOrderId}"</c> (D-S23) y la
/// tienda no guarda el <c>shopId</c>; el reintento manual la obtiene con
/// <see cref="IShopifyConnectionDirectory.FindByShopDomainAsync"/> y el <c>ShopDomain</c> de la tienda.</para>
/// </summary>
public interface IShopifyOrderImporter
{
    Task<ShopifyImportOutcome> ImportAsync(
        ShopifyConnectionRef connection,
        ShopifyStore store,
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot snapshot,
        CancellationToken cancellationToken);
}

/// <summary>Cómo quedó la fila después del intento.</summary>
public sealed record ShopifyImportOutcome(ShopifyImportState State);

/// <summary>Lo que §6.3 compara de una fila: el estado de importación, el pago y la cancelación.</summary>
public sealed record ShopifyMirrorState(ShopifyImportState ImportState, string? FinancialStatus, DateTimeOffset? CancelledAt)
{
    public static ShopifyMirrorState Of(ShopifyOrderMirror row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.ImportState, row.FinancialStatus, row.CancelledAt);
    }
}

/// <summary>Un snapshot que se aplicó sobre una fila que ya existía. <see cref="Previous"/> es la fila antes de
/// <c>ApplySnapshot</c>, que pisa <c>FinancialStatus</c> y <c>CancelledAt</c>; <see cref="Row"/> ya tiene el snapshot nuevo.</summary>
public sealed record ShopifyOrderChange(
    ShopifyConnectionRef Connection, ShopifyStore Store, ShopifyOrderMirror Row, ShopifyMirrorState Previous, ShopifyOrderSnapshot Snapshot);

/// <summary>
/// Spec §6.3: la reacción a un cambio de pago o cancelación sobre una fila que ya existía. La implementa
/// <c>ShopifyOrderChanges</c>, que Bootstrapper registra antes de <c>AddShopifyWebhookProcessing</c>: su <c>TryAdd</c> sólo deja
/// <see cref="NoShopifyOrderChangeReaction"/> si nadie registró otra (lo amarra <c>CompositionRootTests</c>).
/// <para>Corre <b>antes</b> del guardado que persiste el snapshot, en la misma unidad de trabajo: lo que escriba en la fila (p. ej.
/// <c>last_error</c>, <c>MarkCancelled</c>) entra en el mismo commit que el snapshot, y si lanza no se guarda nada, la entrega
/// vuelve con su lease y el reintento ve el mismo estado anterior. Por eso tiene que ser idempotente con lo que ya hizo afuera
/// (el pedido de QEP ya cancelado o ya con ese estado de pago): un fallo después de su efecto externo la vuelve a llamar.</para>
/// </summary>
public interface IShopifyOrderChangeReaction
{
    Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken);
}

/// <summary>La reacción vacía, el registro por defecto de <c>AddShopifyWebhookProcessing</c>. En el host la reemplaza
/// <c>ShopifyOrderChanges</c>.</summary>
public sealed class NoShopifyOrderChangeReaction : IShopifyOrderChangeReaction
{
    public Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Un cliente de QEP como lo necesita el importador (§6.2.1): si existe, si está completo y si está activo. Un cliente
/// encontrado inactivo deja el pedido en espera con <c>customer_inactive</c>.
/// <para>Spec 2026-10-26: <see cref="Created"/> dice que el alta lo creó (no es el dueño del documento que ya existía, H8) y
/// <see cref="Version"/> es la del cliente recién creado, que es la huella del cliente (desvío S7). Un cliente encontrado los trae
/// en su valor por defecto.</para></summary>
public sealed record ShopifyCustomerRef(Guid Id, bool IsComplete, bool IsActive, bool Created = false, long Version = 0);

/// <summary>Los datos de un alta completa (§6.2.1 paso 3), con tipos propios: Shopify no referencia Customers. Mismas reglas
/// que el <c>POST</c> de clientes; <see cref="CityId"/> es la ciudad DIVIPOLA, obligatoria si el país es <c>CO</c>.</summary>
public sealed record ShopifyCompleteCustomer(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid ClassificationId);

/// <summary>
/// El alta completa no se pudo hacer con estos datos: no pasan las reglas del <c>POST</c> de clientes
/// (<see cref="Reason"/> = <c>validation.failed</c>), o la clasificación o la ciudad no existen (el código de Customers). El
/// importador cae al alta incompleta: «el cliente nunca detiene la importación» (§6.2). La lanza el adaptador para que
/// Shopify no tenga que conocer las excepciones de Customers.
/// </summary>
public sealed class ShopifyCustomerNotCompletableException : Exception
{
    public ShopifyCustomerNotCompletableException(string reason, Exception innerException)
        : base($"The customer cannot be created complete: {reason}.", innerException) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Spec 2026-10-15 §6.2.1: buscar y dar de alta compradores en Customers. Adaptador en Bootstrapper sobre
/// <c>ICustomerRepository</c> e <c>ICustomerProvisioning</c>. Todo método recibe <c>tenantId</c> y sólo ve ese tenant.
/// </summary>
public interface IShopifyCustomerDirectory
{
    /// <summary><paramref name="type"/> llega ya normalizado por el importador (<c>NIT</c>, <c>CC</c>, <c>CE</c>,
    /// <c>PASAPORTE</c>, D-S22). Uno que no es de esos, o un número en blanco, devuelve <c>null</c> sin consultar: Customers
    /// lanzaría con un tipo desconocido (H7).</summary>
    Task<ShopifyCustomerRef?> FindByDocumentAsync(Guid tenantId, string type, string number, CancellationToken cancellationToken);

    /// <summary>Por <c>phone_e164</c>, con la regla de D-M7: el más viejo, luego el id menor. <paramref name="phone"/> llega como
    /// lo dio Shopify (el de la dirección se escribe a mano: <c>300 123 4567</c>); el adaptador lo lleva a E.164 con el normalizador
    /// de Customers y <paramref name="country"/> (el de la dirección de envío; sin él, <c>CO</c>). Uno que no normaliza da
    /// <c>null</c> sin consultar.</summary>
    Task<ShopifyCustomerRef?> FindByPhoneAsync(Guid tenantId, string phone, string? country, CancellationToken cancellationToken);

    /// <summary>Por correo exacto sin distinguir mayúsculas; si hay varios, el más viejo (H8).</summary>
    Task<ShopifyCustomerRef?> FindByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken);

    /// <summary>El <c>qep_customer_id</c> que el espejo ya anotó (D-S24): un reintento usa ese cliente y no crea otro.</summary>
    Task<ShopifyCustomerRef?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Si el documento ya lo tiene otro cliente del tenant, devuelve ese. Lanza
    /// <see cref="ShopifyCustomerNotCompletableException"/> cuando los datos no alcanzan para una ficha completa.</summary>
    Task<ShopifyCustomerRef> CreateCompleteAsync(Guid tenantId, ShopifyCompleteCustomer data, CancellationToken cancellationToken);

    /// <summary>Sin nombre, nace «Cliente de Shopify». Sin documento, dirección, clasificación ni CUC.</summary>
    Task<ShopifyCustomerRef> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken);
}

/// <summary>Una variante de Catalog por su código (§6.2.2; variantes 2026-10-20 §2.9): <see cref="TaxPercentage"/> es el de la
/// tasa de su producto, 0 si no tiene o si la tasa no existe (la misma regla con la que cotiza). <see cref="IsActive"/> es
/// «vendible»: el producto y la variante activos.</summary>
public sealed record ShopifyProductRef(Guid VariantId, Guid ProductId, string Code, bool IsActive, int TaxPercentage);

/// <summary>Spec 2026-10-15 §6.2.2 (H11), con variantes (2026-10-20 §2.9): SKU de Shopify = <c>Code</c> de la **variante** de QEP.
/// Adaptador sobre <c>IProductRepository</c> e <c>ITaxRateRepository</c>.</summary>
public interface IShopifyProductLookup
{
    /// <summary>Por SKU exacto y sensible a mayúsculas; uno que no existe en el tenant no aparece en el diccionario. Inactivas
    /// incluidas (variante o producto): el importador las marca <c>product_unmatched</c>.</summary>
    Task<IReadOnlyDictionary<string, ShopifyProductRef>> FindByCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> skus, CancellationToken cancellationToken);
}

/// <summary>La ciudad DIVIPOLA de la dirección de envío, con su departamento: la parte de envío del pedido pide los dos.
/// <see cref="SynonymId"/> es el sinónimo por el que casó, si casó por uno (spec 2026-10-26 D-C13): lo que casa así deja huella
/// para que corregir el sinónimo lo alcance (O8).</summary>
public sealed record ShopifyCityRef(Guid DepartmentId, Guid CityId, Guid? SynonymId = null);

/// <summary>Lo que Geography encontró: el departamento, aunque la ciudad no casara (D-C11: sin él no hay bandeja), y la ciudad si
/// casó por alguno de los tres caminos de §4.1. Sin departamento, los dos <c>null</c>.</summary>
public sealed record ShopifyCityLookup(Guid? DepartmentId, ShopifyCityRef? City);

/// <summary>Spec 2026-10-15 §6.2.1 paso 5 (H10): Geography resuelve la ciudad por nombre dentro de un departamento. Geography
/// es global, sin tenant.</summary>
public interface IShopifyGeographyLookup
{
    /// <summary>El nombre real, el DIVIPOLA y, si ninguno casa, un sinónimo del departamento (§4.1).</summary>
    Task<ShopifyCityLookup> LookupCityAsync(string departmentName, string cityName, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-26 §4.2: una ciudad de Colombia que no casó por ningún camino, con su departamento resuelto, en un pedido que ya
/// entró. <see cref="MirrorOrderId"/> es la fila de <c>shopify.orders</c> (la ocurrencia es idempotente por ella).
/// <see cref="CustomerId"/> es el cliente que <b>esta</b> importación creó incompleto, o <c>null</c> (desvío S4);
/// <see cref="CustomerCityOnly"/> dice que la ciudad era lo único que le faltaba para nacer completo, y
/// <see cref="PartyCityOnly"/>, que lo único que le faltaba a la parte de envío (con correo, desvío S10). <see cref="SampleAddress"/>
/// es la dirección sin nombre ni teléfono del comprador (<see cref="ShopifyUnresolvedCitySample"/>): la ve un operador de otro
/// tenant.
/// </summary>
public sealed record ShopifyUnresolvedCityReport(
    Guid TenantId,
    Guid MirrorOrderId,
    Guid OrderId,
    Guid? CustomerId,
    Guid DepartmentId,
    string CityText,
    string SampleAddress,
    string? ProvinceCode,
    bool PartyCityOnly,
    bool CustomerCityOnly,
    DateTimeOffset SeenAt);

/// <summary>Cómo quedó el reporte. <see cref="Ignored"/> es un texto que se normaliza a vacío: no hay nada que resolver y la
/// importación sigue (no se reintenta por él).</summary>
public enum ShopifyUnresolvedCityOutcome
{
    Recorded,
    AlreadyRecorded,
    SynonymExists,
    Ignored,
}

/// <summary><see cref="SynonymId"/> viene con <see cref="ShopifyUnresolvedCityOutcome.SynonymExists"/>: el texto ya tiene
/// sinónimo (se confirmó entre el match y el reporte).</summary>
public sealed record ShopifyUnresolvedCityResult(ShopifyUnresolvedCityOutcome Outcome, Guid? SynonymId);

/// <summary>Spec 2026-10-26 D-C3: Shopify reporta a la bandeja de Geography al importar. Adaptador en Bootstrapper sobre
/// <c>IUnresolvedCityReporter</c>, que confirma en su propia transacción. Lo que lance se propaga: la importación lanza y la
/// entrega se reintenta, y la ocurrencia es idempotente por la fila del espejo.</summary>
public interface IShopifyUnresolvedCityReporter
{
    Task<ShopifyUnresolvedCityResult> ReportAsync(ShopifyUnresolvedCityReport report, CancellationToken cancellationToken);
}

/// <summary>Una transportadora activa del tenant; <see cref="Name"/> es la foto del nombre de QEP que guarda el pedido.</summary>
public sealed record ShopifyCarrierRef(Guid Id, string Name, bool SupportsCashOnDelivery);

/// <summary>Spec 2026-10-15 §6.2.2 pasos 4 y 5 (H20, H22): transportadoras y la tasa del envío, de Quotations.</summary>
public interface IShopifyShippingLookup
{
    /// <summary>Compara con <c>CarrierName.Normalize</c> contra el nombre y los nombres externos de las transportadoras
    /// <b>activas</b>. Un título en blanco o sin coincidencia da <c>null</c>.</summary>
    Task<ShopifyCarrierRef?> MatchCarrierAsync(Guid tenantId, string title, CancellationToken cancellationToken);

    /// <summary>El porcentaje del IVA del envío del tenant; <c>null</c> sin tasa configurada o con la tasa inactiva
    /// (<c>freight_tax_rate_missing</c>).</summary>
    Task<int?> GetFreightTaxPercentageAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una línea ya preciada (§6.2.2): precio unitario con IVA, descuento en porcentaje con 4 decimales y la tasa del
/// producto de QEP. Por variante (spec 2026-10-20 §4): <paramref name="VariantId"/> es la variante cuyo <c>Code</c> es el SKU de
/// Shopify, y <paramref name="ProductId"/> su producto; viajan los dos porque el pedido guarda ambos en la línea.</summary>
public sealed record ShopifyOrderLineDraft(
    Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

/// <summary>La parte de envío del pedido (§6.2.3 paso 2, H25), sólo cuando la dirección de Shopify quedó completa.</summary>
public sealed record ShopifyPartyDraft(
    string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId);

/// <summary>
/// El envío resuelto por el importador (§6.2.2). <see cref="ChargeMode"/> es el nombre del modo de Quotations (<c>Charged</c>,
/// <c>Free</c>) o <c>null</c> sin envío registrado; «al cobro» nunca sale de la tienda. Con <see cref="IsStorePickup"/> el pedido
/// es de recogida: el adaptador manda envío vacío y sin parte de envío, porque el dominio rechaza cualquier envío con recogida.
/// </summary>
public sealed record ShopifyShippingDraft(
    bool IsStorePickup,
    Guid? CarrierId,
    string? CarrierName,
    string? ChargeMode,
    decimal FreightAmount,
    int FreightTaxPercentage,
    bool CashOnDelivery);

/// <summary>La cuenta de cobro de la tienda, como la guarda Companies; su moneda es la del pedido.</summary>
public sealed record ShopifyBillingAccountRef(Guid CompanyId, string BankName, string AccountNumber, string Currency);

/// <summary>
/// Los datos de <c>ExternalOrderDraft</c> de Quotations con tipos propios (§6.2.3). <see cref="FinancialStatus"/> es el de
/// Shopify tal cual: el adaptador lo traduce (<c>paid</c>, <c>partially_paid</c>, el resto pendiente), y con contraentrega el
/// pedido nace pendiente aunque diga otra cosa (H5). <see cref="ExternalKey"/> es <c>"{shopId}:{shopifyOrderId}"</c> (D-S23).
/// La facturación va siempre a los datos del cliente.
/// </summary>
public sealed record ShopifyOrderDraft(
    Guid TenantId,
    string ExternalReference,
    Guid ClientId,
    Guid AdvisorMembershipId,
    string? PaymentMethod,
    string? Notes,
    ShopifyPartyDraft? ShippingParty,
    ShopifyBillingAccountRef BillingAccount,
    IReadOnlyList<ShopifyOrderLineDraft> Lines,
    string? FinancialStatus,
    DateTimeOffset OccurredAt,
    ShopifyShippingDraft? Shipping,
    string ExternalKey,
    string? ExternalShippingTitle);

/// <summary>Lo que devolvió el alta: el pedido (nuevo o el que ya existía con esa clave, <see cref="AlreadyExisted"/>) o el
/// código con que una regla del pedido lo rechazó (<c>order_rejected</c>, §6.2.3 paso 5). <see cref="ClientId"/> es el cliente
/// del pedido <b>como quedó en Quotations</b>: con <see cref="AlreadyExisted"/> es el del intento que lo creó, que puede no ser el
/// que este intento encontró, y es el que la fila del espejo tiene que anotar. <see cref="ShippingParty"/> es la parte de envío
/// como quedó guardada (spec 2026-10-26 D-C4), también con <see cref="AlreadyExisted"/>: la huella sale de acá, nunca del
/// borrador.</summary>
public sealed record ShopifyOrderWriteResult
{
    private ShopifyOrderWriteResult(
        Guid orderId,
        string? orderNumber,
        decimal total,
        bool alreadyExisted,
        Guid clientId,
        string? rejectedCode,
        ShopifyShippingFingerprint? shippingParty)
    {
        OrderId = orderId;
        OrderNumber = orderNumber;
        Total = total;
        AlreadyExisted = alreadyExisted;
        ClientId = clientId;
        RejectedCode = rejectedCode;
        ShippingParty = shippingParty;
    }

    public ShopifyShippingFingerprint? ShippingParty { get; }

    public Guid OrderId { get; }

    public Guid ClientId { get; }

    public string? OrderNumber { get; }

    public decimal Total { get; }

    public bool AlreadyExisted { get; }

    public string? RejectedCode { get; }

    public bool IsRejected => RejectedCode is not null;

    public static ShopifyOrderWriteResult Written(
        Guid orderId,
        string orderNumber,
        decimal total,
        bool alreadyExisted,
        Guid clientId,
        ShopifyShippingFingerprint? shippingParty = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(clientId, Guid.Empty);
        return new(orderId, orderNumber, total, alreadyExisted, clientId, null, shippingParty);
    }

    public static ShopifyOrderWriteResult Rejected(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new(Guid.Empty, null, 0m, false, Guid.Empty, code, null);
    }
}

public enum ShopifyOrderOutcomeKind
{
    Applied,
    Unchanged,
    Ignored,
    AlreadyInvoiced,
    AlreadyCancelled,
}

/// <summary>Cómo terminó un cambio posterior (§6.3). <see cref="ShopifyOrderOutcomeKind.Ignored"/> lleva el código de la regla
/// del pedido que lo rechazó (<c>order.order.not_pending</c>, <c>order.order.payment_has_proofs</c>), que va a <c>last_error</c>
/// del espejo. <see cref="ShopifyOrderOutcomeKind.Unchanged"/> es un <c>financial_status</c> que no cambia nada (<c>refunded</c>,
/// <c>partially_refunded</c>, <c>voided</c>): no lleva código ni deja <c>last_error</c>, y Quotations no se llama.</summary>
public sealed record ShopifyOrderOutcome(ShopifyOrderOutcomeKind Kind, string? Code)
{
    public static ShopifyOrderOutcome Applied { get; } = new(ShopifyOrderOutcomeKind.Applied, null);

    public static ShopifyOrderOutcome Unchanged { get; } = new(ShopifyOrderOutcomeKind.Unchanged, null);

    public static ShopifyOrderOutcome AlreadyInvoiced { get; } = new(ShopifyOrderOutcomeKind.AlreadyInvoiced, "order.order.already_invoiced");

    public static ShopifyOrderOutcome AlreadyCancelled { get; } = new(ShopifyOrderOutcomeKind.AlreadyCancelled, "order.order.already_cancelled");

    public static ShopifyOrderOutcome Ignored(string code) => new(ShopifyOrderOutcomeKind.Ignored, code);
}

/// <summary>
/// Spec 2026-10-15 §6.2 y §6.3: el único camino de Shopify a los pedidos de Quotations. Adaptador en Bootstrapper
/// (<c>ShopifyOrderWriter</c>) sobre <c>IExternalOrderWriter</c>; ningún consumidor referencia Quotations.Application.
/// <para><b>Cada llamada corre en su propio scope de DI.</b> <c>IExternalOrderWriter</c>, ante la carrera de la clave externa,
/// llama a <c>IQuotationsUnitOfWork.DiscardPendingChanges()</c>, que limpia TODO el tracker de Quotations del scope: si el
/// llamador tuviera algo pendiente ahí, se perdería en silencio. Con un scope propio no hay nada más en ese tracker. Por lo
/// mismo, cada llamada confirma lo suyo: no se suma a la unidad de trabajo de Shopify del llamador.</para>
/// <para><b>Qué se traduce y qué se propaga.</b> Sólo los rechazos deterministas de una regla del pedido se vuelven resultado
/// (<see cref="ShopifyOrderWriteResult.Rejected"/>, <see cref="ShopifyOrderOutcome"/>). <c>RequestConcurrencyException</c>,
/// <c>order.order.number_taken</c> (otra alta tomó el consecutivo a la vez) y cualquier otro error transitorio (base, red,
/// cancelación) <b>se propagan</b> sin traducir: el llamador no guarda la fila y la entrega o la conciliación reintentan.
/// Por eso quien llame tiene que ser idempotente con lo que ya pasó en Quotations: el alta lo es por
/// <c>ExternalKey</c>, y una sincronización o una anulación repetidas dan <see cref="ShopifyOrderOutcome.Applied"/> o
/// <see cref="ShopifyOrderOutcome.AlreadyCancelled"/>.</para>
/// </summary>
public interface IShopifyOrderWriter
{
    Task<ShopifyOrderWriteResult> CreateAsync(ShopifyOrderDraft draft, CancellationToken cancellationToken);

    /// <summary>Sólo cambia un pedido <c>Pending</c> sin comprobantes; con el mismo estado no hace nada y da
    /// <see cref="ShopifyOrderOutcome.Applied"/>. <c>refunded</c>, <c>partially_refunded</c> y <c>voided</c> dan
    /// <see cref="ShopifyOrderOutcome.Unchanged"/> sin llamar a Quotations.</summary>
    Task<ShopifyOrderOutcome> SyncPaymentStatusAsync(
        Guid tenantId, Guid orderId, string financialStatus, CancellationToken cancellationToken);

    Task<ShopifyOrderOutcome> CancelAsync(
        Guid tenantId, Guid orderId, Guid byMembershipId, string reason, CancellationToken cancellationToken);

    /// <summary>D-S23: el pedido de Shopify que ya existe en QEP con esa clave, como <see cref="ShopifyOrderWriteResult.Written"/>
    /// con <c>AlreadyExisted</c>, o <c>null</c>. Sólo lee. Lo usa §6.3 antes de ignorar una fila en espera que se canceló: si el
    /// worker cayó entre crear el pedido y marcar la fila, el pedido de QEP existe y hay que anularlo, no ignorarlo.</summary>
    Task<ShopifyOrderWriteResult?> FindByExternalKeyAsync(Guid tenantId, string externalKey, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-15 §6.1 y §6.2: el asesor de la tienda es una membresía del tenant. Adaptador sobre Tenancy e
/// Identity.</summary>
public interface IShopifyMemberDirectory
{
    /// <summary><c>true</c> sólo si la membresía es del tenant y está <c>Active</c> (<c>advisor_inactive</c>).</summary>
    Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>El nombre visible de la membresía o, sin él, el correo del usuario. <c>null</c> si no es del tenant.</summary>
    Task<string?> NameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-15 §6.1: la cuenta de cobro de la tienda se valida contra Companies, con la misma comparación que
/// Quotations (banco sin mayúsculas, número exacto, los dos recortados).</summary>
public interface IShopifyBillingAccounts
{
    /// <summary><c>null</c> si la empresa no es del tenant o la cuenta ya no está (<c>billing_account_missing</c>).</summary>
    Task<ShopifyBillingAccountRef?> FindAsync(
        Guid tenantId, Guid companyId, string bank, string number, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-15 §7: el número <c>PED-…</c> de los pedidos de QEP que la bandeja muestra en cada fila importada.
/// Adaptador en Bootstrapper (<c>ShopifyOrderNumbers</c>) sobre <c>IOrderRepository</c>, con una sola consulta por página.</summary>
public interface IShopifyOrderNumbers
{
    /// <summary>Sólo los pedidos de <paramref name="tenantId"/>; uno que no existe (o es de otro tenant) no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);
}

/// <summary>Una conexión de Shopify del tenant como la lista la pantalla de tiendas (§7): <see cref="Status"/> es el nombre del
/// estado de Integrations (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>), tal cual. <see cref="ShopDomain"/> es
/// <c>null</c> si la conexión no lo guardó, nunca <c>""</c>.</summary>
public sealed record ShopifyConnectionSummary(Guid ConnectionId, string Name, string? ShopDomain, string Status);

/// <summary>Spec 2026-10-15 §7 <c>GET /stores</c>: las conexiones de Shopify del tenant, en cualquier estado. Adaptador en
/// Bootstrapper sobre <c>IIntegrationConnectionRepository</c>: Shopify no referencia Integrations.</summary>
public interface IShopifyTenantConnections
{
    Task<IReadOnlyList<ShopifyConnectionSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-15 §6.1 y §7: la empresa de la cuenta de cobro y la clasificación por defecto de la tienda, por sus
/// nombres. Un nombre <c>null</c> es que no existe en ese tenant: el <c>PUT</c> lo responde
/// <c>shopify.store.classification_not_found</c>. Adaptador en Bootstrapper sobre Companies y Customers.</summary>
public interface IShopifyStoreReferences
{
    Task<string?> CompanyNameAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    Task<string?> ClassificationNameAsync(Guid tenantId, Guid classificationId, CancellationToken cancellationToken);
}

/// <summary>Un sinónimo de Geography como lo necesita el trabajo de corrección: su ciudad <b>actual</b> y si está borrado (spec
/// 2026-10-26 D-C6).</summary>
public sealed record ShopifyCitySynonymRef(Guid SynonymId, Guid DepartmentId, Guid CityId, bool IsDeleted);

/// <summary>Spec 2026-10-26: el trabajo lee el sinónimo al correr, no al sembrarse. Adaptador en Bootstrapper sobre
/// <c>ICitySynonymRepository</c>, con un scope propio por llamada: el repositorio rastrea, y la segunda lectura de un mismo trabajo
/// (después de guardar, por si el operador corrigió el sinónimo en el medio) tiene que ver la base, no la primera lectura.</summary>
public interface IShopifyCitySynonymLookup
{
    /// <summary>Borrados incluidos (<see cref="ShopifyCitySynonymRef.IsDeleted"/>); <c>null</c> si no existe.</summary>
    Task<ShopifyCitySynonymRef?> FindAsync(Guid synonymId, CancellationToken cancellationToken);
}

/// <summary>Qué pasó con una escritura del trabajo de corrección. <see cref="Applied"/> y <see cref="AlreadyApplied"/> son éxito
/// (la segunda es un reintento: Quotations o Customers ya lo tenían); el resto son las razones por las que el pedido o el cliente ya
/// no admiten el cambio, y terminan el trabajo <c>Skipped</c>.</summary>
public enum ShopifyCityWriteKind
{
    Applied,
    AlreadyApplied,
    NotFound,
    NotPending,
    StorePickup,
    ShippingPresent,
    ShippingChanged,
    PartyIncomplete,
    NotEligible,
    NotCompletable,
}

/// <summary><see cref="Party"/> es la parte de envío como quedó guardada, con un éxito de pedido: es la huella.
/// <see cref="CustomerVersion"/> y <see cref="CustomerCityId"/> son la versión y la ciudad del cliente como quedaron, con un
/// éxito de cliente (desvío S7). Con <c>AlreadyApplied</c> la ciudad, en los dos casos, es la que escribió un intento anterior y
/// puede no ser la que se pidió: la huella guarda ésa, y el paso 5 la recorrige. <see cref="Detail"/> es el motivo de Quotations
/// o Customers (el estado del pedido, <c>edited</c>, <c>validation.failed</c>…), para el log.</summary>
public sealed record ShopifyCityWriteResult(
    ShopifyCityWriteKind Kind, ShopifyShippingFingerprint? Party, long? CustomerVersion, string? Detail, Guid? CustomerCityId = null);

/// <summary>
/// Spec 2026-10-26 D-C12: lo que el recorrido de la historia previa lee de un pedido ya importado. <see cref="Status"/> es el
/// nombre del enum de Quotations (<c>Pending</c>, <c>Approved</c>…). <see cref="NotesHaveRawAddress"/>: alguna línea de las notas
/// empieza por <see cref="ShopifyOrderImporter.RawAddressLabel"/>, que es la marca de que la dirección no quedó como parte de
/// envío (la importación la deja mientras no hay parte, y poner la parte la quita).
/// </summary>
public sealed record ShopifyOrderCityState(string Status, bool IsStorePickup, bool HasShippingParty, bool NotesHaveRawAddress);

/// <summary>
/// Spec 2026-10-26 §4.4 y §4.6: lo que el trabajo de corrección escribe en Quotations y en Customers. Adaptador en Bootstrapper sobre
/// <c>IExternalOrderWriter</c> e <c>ICustomerProvisioning</c>, <b>un scope propio por llamada</b> (como <c>ShopifyOrderWriter</c>): cada
/// llamada confirma lo suyo y no se suma a la unidad de trabajo de Shopify. Las razones por las que no se puede vuelven en el
/// resultado; la concurrencia (<c>concurrency.conflict</c>) y los errores transitorios se propagan y el trabajo reintenta.
/// </summary>
public interface IShopifyCityCorrectionWriter
{
    /// <summary>Pone la parte de envío que el pedido no tenía y quita de las notas las líneas que empiezan por
    /// <paramref name="notesLinePrefix"/>.</summary>
    Task<ShopifyCityWriteResult> AttachShippingAsync(
        Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken);

    /// <summary>Lleva la parte de envío a la ciudad nueva sólo si sigue siendo <paramref name="expected"/>.</summary>
    Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken);

    /// <summary>Completa al cliente que la importación creó incompleto, sólo si sigue en <paramref name="expectedVersion"/>.
    /// <paramref name="requestedCityIds"/> son las ciudades que este trabajo pidió hasta ahora: con ellas Customers reconoce su
    /// propio reintento (una ciudad que no pidió la puso una persona).</summary>
    Task<ShopifyCityWriteResult> CompleteCustomerAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        ShopifyCompleteCustomer data,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Lleva al cliente de <paramref name="previousCityId"/> a <paramref name="cityId"/> sólo si sigue en
    /// <paramref name="expectedVersion"/>; <paramref name="requestedCityIds"/> como en <see cref="CompleteCustomerAsync"/>.</summary>
    Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
        Guid tenantId,
        Guid customerId,
        long expectedVersion,
        Guid previousCityId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-26 D-C12: el estado del pedido para el recorrido de la historia previa, o <c>null</c> si ya no existe
    /// en ese tenant. Sólo lee.</summary>
    Task<ShopifyOrderCityState?> ReadOrderCityStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
