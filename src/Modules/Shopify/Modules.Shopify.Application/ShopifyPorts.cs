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
    /// ruta del webhook: el cron no corre en nombre de ningún tenant. Deja fuera las que están operativas sin token (spec
    /// 2026-11-12 §6.4): el cron no tiene con qué llamar a Shopify.</summary>
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
/// <para>Spec 2026-11-12 §6.4: <see cref="IsOperational"/> <b>no</b> implica <see cref="AccessToken"/>. Con el token vencido
/// —el refresco de Integrations va tarde— la conexión sale operativa y sin token: la ingesta y el redact la tratan como
/// operativa (importan, no borran), y quien llame a la Admin API salta la corrida sin reportar nada.</para>
/// </summary>
public sealed record ShopifyConnectionRef(
    Guid TenantId, Guid ConnectionId, string ShopId, string ShopDomain, bool IsOperational, string? AccessToken)
{
    /// <summary>D-S4 y §5.3: el instante del enlace (la creación de la conexión). La tienda que el módulo ve por
    /// primera vez arranca ahí su ventana de conciliación, para que un pedido hecho entre el enlace y el primer contacto entre
    /// aunque su webhook se pierda. Nulo si el adaptador no lo sabe: entonces, «ahora».</summary>
    public DateTimeOffset? LinkedAt { get; init; }

    /// <summary>Spec 2026-11-17 §5.1: los scopes concedidos (<c>ShopifyFieldKeys.Scopes</c>, separados por coma). Vacío si el
    /// adaptador no los sabe. <see cref="ShopifyScopes.CanReadProducts"/> decide con ellos.</summary>
    public IReadOnlyCollection<string> Scopes { get; init; } = [];

    /// <summary>Spec 2026-11-17 (onboarding) §2, plan D1: el perfil de la tienda que guardó Integrations (el campo interno
    /// <c>shopProfile</c>); <c>null</c> si nunca se leyó o está roto. Lleva correo y teléfono: <see cref="ToString"/> lo
    /// omite.</summary>
    public ShopifyShopProfile? Profile { get; init; }

    public override string ToString() =>
        $"ShopifyConnectionRef {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, ShopId = {ShopId}, ShopDomain = {ShopDomain}, IsOperational = {IsOperational} }}";
}

/// <summary>
/// Spec 2026-11-17 (onboarding) §2: el perfil de la tienda como lo ve este módulo. Copia de forma del de Integrations
/// (<c>Modules.Integrations.Application.Shopify.ShopifyShopProfile</c>), que Shopify no ve; <c>ShopifyConnectionDirectory</c>
/// (Bootstrapper) mapea campo por campo. <see cref="Email"/>, <see cref="ContactEmail"/> y el teléfono son datos personales del
/// comerciante: no van a logs, auditoría ni eventos.
/// </summary>
public sealed record ShopifyShopProfile(
    string? Email, string? ContactEmail, string? CurrencyCode, bool? TaxesIncluded, ShopifyShopAddress? BillingAddress);

/// <summary>La dirección de facturación de la tienda. <see cref="CountryCode"/> es el <c>countryCodeV2</c> de Shopify.</summary>
public sealed record ShopifyShopAddress(
    string? Company,
    string? Address1,
    string? Address2,
    string? City,
    string? Province,
    string? ProvinceCode,
    string? CountryCode,
    string? Phone);

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

/// <summary>
/// Spec 2026-11-14 §3: la detección de ediciones sobre un pedido ya importado. La implementa <c>ShopifyOrderEdits</c>, que
/// Bootstrapper registra antes de <c>AddShopifyWebhookProcessing</c> (que sólo deja <see cref="NoShopifyOrderEditDetector"/> si nadie
/// registró otro). La ingesta la llama en dos pasos:
/// <list type="number">
/// <item><see cref="DetectAsync"/>, después del espejo y antes de la reacción de §6.3, en la misma unidad de trabajo: el cambio y la
/// fila entran en el mismo commit que el snapshot. No escribe en el pedido de QEP.</item>
/// <item><see cref="ApplyPendingAsync"/>, después de ese guardado: con la tienda automática aplica el cambio pendiente. El pedido lo
/// confirma Quotations por su cuenta (otro scope), así que si el guardado que sigue falla, el cambio ya guardado queda pendiente y el
/// reintento lo termina (review I-1).</item>
/// </list>
/// </summary>
public interface IShopifyOrderEditDetector
{
    /// <summary><paramref name="previousPayload"/> es el payload de la fila antes de <c>ApplySnapshot</c>: la base de una fila
    /// importada antes de la sincronización (sin versión aplicada, plan D6).</summary>
    Task DetectAsync(ShopifyOrderChange change, string previousPayload, CancellationToken cancellationToken);

    /// <summary>Aplica el cambio pendiente del snapshot vigente de <paramref name="row"/> si la tienda es automática. Devuelve si
    /// tocó algo que la ingesta tiene que guardar.</summary>
    Task<bool> ApplyPendingAsync(ShopifyStore store, ShopifyOrderMirror row, CancellationToken cancellationToken);
}

/// <summary>La detección vacía, el registro por defecto de <c>AddShopifyWebhookProcessing</c>. En el host la reemplaza
/// <c>ShopifyOrderEdits</c>.</summary>
public sealed class NoShopifyOrderEditDetector : IShopifyOrderEditDetector
{
    public Task DetectAsync(ShopifyOrderChange change, string previousPayload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> ApplyPendingAsync(ShopifyStore store, ShopifyOrderMirror row, CancellationToken cancellationToken) =>
        Task.FromResult(false);
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

    /// <summary>Por id de variante (spec 2026-11-13: la línea personalizada asociada); uno que no existe en el tenant no aparece.
    /// Inactivas incluidas, con <c>IsActive = false</c> (vendible, como <c>FindByCodesAsync</c>). La tasa, con la misma regla.</summary>
    Task<IReadOnlyDictionary<Guid, ShopifyProductRef>> FindByVariantIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17: los códigos de esa lista que ya usa alguna variante del tenant, en una consulta (la cobertura).</summary>
    Task<IReadOnlySet<string>> FindExistingCodesAsync(Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17: cuántos productos activos tiene el tenant (<c>catalogEmpty</c> de la cobertura).</summary>
    Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Lo que pinta la lista de asociaciones de líneas personalizadas (spec 2026-11-13, D3/D4): <see cref="DisplayName"/> es
/// el nombre visible de la variante (producto más valores de opción).</summary>
public sealed record ShopifyVariantLabel(Guid VariantId, string DisplayName, string Code, bool IsActive);

/// <summary>Nombre y código de variantes de Catalog para la bandeja de Shopify; adaptador en Bootstrapper.</summary>
public interface IShopifyVariantLabels
{
    /// <summary>Una variante inexistente o de otro tenant no aparece: quien llama la pinta como borrada (D5).</summary>
    Task<IReadOnlyDictionary<Guid, ShopifyVariantLabel>> DescribeAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
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

    /// <summary>Spec 2026-11-14 §5: los nombres de QEP (DIVIPOLA) de una ciudad y su departamento, para mandarlos a Shopify; <c>null</c>
    /// si alguno no existe o la ciudad es de otro departamento (owner 2026-11-15).</summary>
    Task<ShopifyCityName?> DescribeCityAsync(Guid departmentId, Guid cityId, CancellationToken cancellationToken);

    /// <summary>Owner 2026-11-15: la corrección del pedido valida que el departamento exista y que la ciudad exista y sea de él.</summary>
    Task<ShopifyCityCheck> CheckCityAsync(Guid departmentId, Guid cityId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-24 §11.1: la región de una dirección de fuera de Colombia, con su país y su código oficial en él (el
    /// INE en España); <c>null</c> si no existe. Con <see cref="ShopifyShippingAddressRules"/> sale el <c>provinceCode</c>.</summary>
    Task<(string CountryCode, string Code)?> DescribeRegionAsync(Guid regionId, CancellationToken cancellationToken);

    /// <summary>El camino de vuelta, para la dirección que llega de Shopify: el id de la región con ese código oficial en ese país
    /// (<paramref name="countryCode"/> en mayúscula), o <c>null</c> si Geography no la tiene.</summary>
    Task<Guid?> FindRegionAsync(string countryCode, string regionCode, CancellationToken cancellationToken);
}

/// <summary>Una ciudad de Geography por sus nombres, como los guarda la semilla (mayúsculas y con tildes).</summary>
public sealed record ShopifyCityName(string City, string Department);

/// <summary>El resultado de <see cref="IShopifyGeographyLookup.CheckCityAsync"/>: cada falla marca un campo distinto del formulario
/// (el departamento se elige primero, la ciudad después).</summary>
public enum ShopifyCityCheck
{
    Matches,
    DepartmentNotFound,
    CityNotInDepartment,
}

/// <summary>Spec 2026-11-14 §5 y plan D12: la dirección de envío de <c>orderUpdate</c> (<c>MailingAddressInput</c>, que no tiene
/// <c>name</c>): el nombre de QEP en <see cref="FirstName"/> y la dirección en <see cref="Address1"/>, con <see cref="LastName"/> y
/// <see cref="Address2"/> vacíos. <c>orderUpdate</c> reemplaza la dirección entera, así que <see cref="Company"/> y
/// <see cref="CountryCode"/> salen del último snapshot (Review Focus 3). <see cref="Zip"/> es el código postal de la parte de
/// QEP o, sin él, el del último snapshot si país y provincia no cambiaron (spec 2026-11-24); en <c>null</c> no viaja.</summary>
public sealed record ShopifyAddressUpdate(
    string FirstName,
    string LastName,
    string Address1,
    string Address2,
    string City,
    string? ProvinceCode,
    string CountryCode,
    string Phone,
    string? Company,
    string? Zip = null);

/// <summary>Lo que viaja en un <c>orderUpdate</c>: cada grupo en <c>null</c> (la nota, con <see cref="SetNote"/> en
/// <c>false</c>) no se manda, y Shopify lo conserva.</summary>
public sealed record ShopifyOrderUpdate(
    long ShopifyOrderId,
    ShopifyAddressUpdate? ShippingAddress,
    bool SetNote,
    string? Note,
    IReadOnlyDictionary<string, string>? Attributes);

public enum ShopifyOrderUpdateKind
{
    Updated,
    Retry,
    Failed,
}

/// <summary><see cref="FailureCode"/> viene con <see cref="ShopifyOrderUpdateKind.Failed"/> (<c>access_denied</c>,
/// <c>user_error</c>, <c>order_not_found</c>, <c>rejected</c>); <see cref="RetryAfter"/>, a veces, con
/// <see cref="ShopifyOrderUpdateKind.Retry"/>.</summary>
public sealed record ShopifyOrderUpdateResult(ShopifyOrderUpdateKind Kind, string? FailureCode, TimeSpan? RetryAfter);

/// <summary>Spec 2026-11-14 §5: la mutación <c>orderUpdate</c> de la Admin API GraphQL. Adaptador en Infrastructure
/// (<c>ShopifyOrderUpdateClient</c>).</summary>
public interface IShopifyOrderUpdater
{
    /// <summary>Sólo manda los campos no nulos (<see cref="ShopifyOrderUpdate.SetNote"/> para la nota). Nunca registra el token, el
    /// cuerpo ni el mensaje de los <c>userErrors</c>, que puede traer datos del comprador.</summary>
    Task<ShopifyOrderUpdateResult> UpdateAsync(ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken);
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

/// <summary>La parte de envío del pedido (§6.2.3 paso 2, H25), sólo cuando la dirección de Shopify quedó completa.
/// <paramref name="PostalCode"/> es el zip de la dirección (<see cref="ShopifyOrderImporter.ShippingPostalCode"/>): sin él, una
/// edición de dirección hecha en Shopify borraría el código postal del pedido. Los campos y su orden son los de
/// <see cref="ShopifyShippingFingerprint"/>: en Colombia van <paramref name="DepartmentId"/> y <paramref name="CityId"/>; fuera,
/// <paramref name="Country"/>, <paramref name="CityName"/> y, si se pudo traducir la provincia, <paramref name="RegionId"/>.</summary>
public sealed record ShopifyPartyDraft(
    string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId, string? PostalCode = null,
    string? Country = null, string? CityName = null, Guid? RegionId = null);

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

/// <summary>La cuenta de cobro de la tienda, como la guarda Companies; su moneda es la del pedido. Banco y número en null los
/// dos es la empresa sin cuenta (spec 2026-11-17 §4.4). <see cref="ChargesVat"/> es si la empresa cobra IVA hoy: el importador
/// precia con él, y el pedido lo congela al crearse.</summary>
public sealed record ShopifyBillingAccountRef(
    Guid CompanyId, string? BankName, string? AccountNumber, string Currency, bool ChargesVat = true);

/// <summary>La empresa de una tienda sin cuenta bancaria (spec 2026-11-17 §4.4), con su IVA.</summary>
public sealed record ShopifyBillingCompanyRef(Guid CompanyId, bool ChargesVat);

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
    string? ExternalShippingTitle,
    string? BuyerNotes = null);

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
/// del pedido que lo rechazó (<c>order.order.not_editable</c>, <c>order.order.payment_has_proofs</c>), que va a <c>last_error</c>
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

/// <summary>Spec 2026-11-14: una línea del pedido de QEP como la guardó Quotations (precio con IVA, descuento en porcentaje).</summary>
public sealed record ShopifyOrderLineState(
    Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

/// <summary>
/// Spec 2026-11-14 §3 y §4: el pedido de QEP como lo compara la sincronización de ediciones, con tipos propios.
/// <see cref="Status"/> y <see cref="PaymentStatus"/> viajan por nombre (<c>Open</c>, <c>Archived</c>, <c>Cancelled</c>;
/// <c>PaymentPending</c>…): con <see cref="IsPrepared"/> e <see cref="IsInvoiced"/> arman el motivo por el que no se edita (plan
/// D20). <see cref="Shipping"/> es el envío como el borrador del importador, y <see cref="ShippingParty"/> la parte como quedó
/// guardada (<c>null</c> sin parte). <see cref="ShippingCountry"/> es el país de la parte; <c>null</c> es Colombia (D21).
/// </summary>
public sealed record ShopifyOrderState(
    Guid OrderId,
    long Version,
    string Status,
    bool IsEditable,
    bool IsPrepared,
    bool IsInvoiced,
    string PaymentStatus,
    Guid ClientId,
    string Currency,
    bool IsStorePickup,
    IReadOnlyList<ShopifyOrderLineState> Lines,
    ShopifyShippingDraft Shipping,
    ShopifyShippingFingerprint? ShippingParty,
    string? ShippingCountry,
    string? BuyerNotes,
    // Spec 2026-11-17 R7: el IVA congelado del pedido. El planificador precia con él, nunca con el de la empresa hoy.
    bool ChargesVat = true);

/// <summary>Spec 2026-11-14 §4: los grupos de un cambio que se aplican al pedido; cada uno en <c>null</c> no se toca.
/// <see cref="BuyerNotes"/> sólo cuenta con <see cref="ReplaceBuyerNotes"/>. <see cref="ExpectedVersion"/> es la versión del
/// pedido con la que se planeó.</summary>
public sealed record ShopifyOrderEditDraft(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    IReadOnlyList<ShopifyOrderLineDraft>? Lines,
    ShopifyShippingDraft? Shipping,
    ShopifyPartyDraft? ShippingParty,
    bool ReplaceBuyerNotes,
    string? BuyerNotes,
    DateTimeOffset OccurredAt);

/// <summary><see cref="State"/> es el pedido después de escribir (la proyección sale de acá, nunca del borrador);
/// <see cref="RejectedCode"/> es la regla del pedido que lo rechazó (<c>order.order.not_editable</c>,
/// <c>order.order.store_pickup</c>, <c>order.order.items_required</c>…).</summary>
public sealed record ShopifyOrderEditOutcome(ShopifyOrderState? State, string? RejectedCode);

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

    /// <summary>Spec 2026-11-14: el pedido de QEP como lo compara la sincronización de ediciones, o <c>null</c> si no existe en
    /// ese tenant. Sólo lee.</summary>
    Task<ShopifyOrderState?> ReadStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-14 §4: aplica los grupos de un cambio. Las reglas del pedido vuelven en
    /// <see cref="ShopifyOrderEditOutcome.RejectedCode"/>; <c>concurrency.conflict</c> (el pedido cambió desde que se planeó) se
    /// propaga.</summary>
    Task<ShopifyOrderEditOutcome> ApplyEditAsync(ShopifyOrderEditDraft draft, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-14 §5: lo que el asesor corrige en el pedido de QEP de una fila del espejo. <see cref="ShippingParty"/> en
/// <c>null</c> no toca la parte; <see cref="BuyerNotes"/> sólo cuenta con <see cref="ReplaceBuyerNotes"/>. <see cref="MemberId"/>
/// firma el historial y <see cref="SubjectId"/> la auditoría; <see cref="ExpectedVersion"/> es el <c>If-Match</c> del pedido.</summary>
public sealed record ShopifyOrderCorrection(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    ShopifyPartyDraft? ShippingParty,
    bool ReplaceBuyerNotes,
    string? BuyerNotes,
    Guid MemberId,
    Guid SubjectId);

/// <summary>
/// Spec 2026-11-14 §5: la corrección del asesor escribe el pedido de QEP <b>dentro de la transacción de Shopify</b>, para que el
/// pedido, los atributos de la fila del espejo y el envío encolado queden juntos o no queden. Adaptador en Bootstrapper
/// (<c>ShopifyOrderCorrectionWriter</c>), scoped y <b>sin scope propio</b> (al revés que <see cref="IShopifyOrderWriter"/>): enrola el
/// <c>QuotationsDbContext</c> del request en la transacción abierta del <c>ShopifyDbContext</c>, como <c>CatalogProductDeletionCleanup</c>;
/// sin transacción abierta lanza <see cref="InvalidOperationException"/>. Las reglas del pedido (<c>QuotationsDomainException</c>, 422)
/// y <c>RequestConcurrencyException</c> (412) se propagan.
/// </summary>
public interface IShopifyOrderCorrectionWriter
{
    Task<ShopifyOrderState> CorrectAsync(ShopifyOrderCorrection correction, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-15 §6.1 y §6.2: el asesor de la tienda es una membresía del tenant. Adaptador sobre Tenancy e
/// Identity.</summary>
public interface IShopifyMemberDirectory
{
    /// <summary><c>true</c> sólo si la membresía es del tenant y está <c>Active</c> (<c>advisor_inactive</c>).</summary>
    Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>El nombre visible de la membresía o, sin él, el correo del usuario. <c>null</c> si no es del tenant.</summary>
    Task<string?> NameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17 (onboarding) §4.1: las membresías activas del tenant con su nombre (el de
    /// <see cref="NameAsync"/>), en orden de nombre. Son las opciones del paso «Asesor».</summary>
    Task<IReadOnlyList<ShopifyMemberOption>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una membresía activa elegible como asesor de la tienda.</summary>
public sealed record ShopifyMemberOption(Guid MembershipId, string Name);

/// <summary>Spec 2026-10-15 §6.1: la cuenta de cobro de la tienda se valida contra Companies, con la misma comparación que
/// Quotations (banco sin mayúsculas, número exacto, los dos recortados).</summary>
public interface IShopifyBillingAccounts
{
    /// <summary><c>null</c> si la empresa no es del tenant o la cuenta ya no está (<c>billing_account_missing</c>).</summary>
    Task<ShopifyBillingAccountRef?> FindAsync(
        Guid tenantId, Guid companyId, string bank, string number, CancellationToken cancellationToken);

    /// <summary>La empresa sola, para una tienda sin cuenta (spec 2026-11-17 §4.4); <c>null</c> si no es del tenant
    /// (<c>billing_account_missing</c> en el importador, <c>billing_account_not_found</c> al configurar).</summary>
    Task<ShopifyBillingCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
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

    /// <summary>Spec 2026-11-17 (onboarding) §4.1: las clasificaciones activas del tenant, en orden de nombre. Son las opciones
    /// del paso «Compradores».</summary>
    Task<IReadOnlyList<ShopifyNamedRef>> ListClassificationsAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Un id con su nombre visible.</summary>
public sealed record ShopifyNamedRef(Guid Id, string Name);

/// <summary>Una cuenta bancaria de una empresa, como la elige o la crea el asistente.</summary>
public sealed record ShopifySetupBankAccount(string BankName, string AccountNumber, string Currency);

/// <summary>Una empresa activa del tenant como la dibuja el paso «Empresa» del asistente.</summary>
/// <param name="DocumentType">El nombre del tipo de documento de Companies (<c>NIT</c>, <c>CC</c>…).</param>
/// <param name="Country">ISO 3166-1 alfa-2.</param>
public sealed record ShopifySetupCompanyRef(
    Guid Id,
    string Name,
    string TaxId,
    string DocumentType,
    string Country,
    bool ChargesVat,
    IReadOnlyList<ShopifySetupBankAccount> BankAccounts);

/// <summary>Lo que el asistente manda para crear la empresa (spec 2026-11-17 onboarding §4.2, plan D5). Las reglas son las de
/// Companies: el validador de <c>CreateCompanyCommand</c> responde con sus claves.</summary>
/// <param name="CityId">Obligatoria en <c>CO</c>.</param>
/// <param name="CityName">El texto de la ciudad fuera de <c>CO</c>.</param>
/// <param name="BankAccount">La cuenta de cobro, si viene (spec §7 H3); se guarda en la empresa.</param>
public sealed record ShopifySetupCompanyDraft(
    Guid TenantId,
    string Name,
    string TaxId,
    string? DocumentType,
    string Country,
    Guid? CityId,
    string? CityName,
    string? Address,
    string? Phone,
    string? Email,
    bool ChargesVat,
    ShopifySetupBankAccount? BankAccount);

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2. Adaptador en Bootstrapper sobre Companies. <see cref="CreateAsync"/> corre
/// <c>ICommandHandler&lt;CreateCompanyCommand, CompanyDto&gt;</c> (autorización, validador, ciudad, siembra de tasas): sus
/// excepciones salen tal cual (<c>ValidationException</c> con las claves de Companies, <c>CompaniesDomainException</c>,
/// <c>RequestForbiddenException</c>).
/// </summary>
public interface IShopifySetupCompanies
{
    /// <summary>Las empresas activas del tenant, por nombre.</summary>
    Task<IReadOnlyList<ShopifySetupCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>La empresa si es del tenant y está activa; si no, <c>null</c>.</summary>
    Task<ShopifySetupCompanyRef?> FindActiveAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    Task<ShopifySetupCompanyRef> CreateAsync(ShopifySetupCompanyDraft draft, CancellationToken cancellationToken);
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
/// nombre del enum de Quotations (<c>Open</c>, <c>Archived</c>, <c>Cancelled</c>). <see cref="NotesHaveRawAddress"/>: alguna línea
/// de las notas empieza por <see cref="ShopifyOrderImporter.RawAddressLabel"/>, que es la marca de que la dirección no quedó como
/// parte de envío (la importación la deja mientras no hay parte, y poner la parte la quita). <see cref="IsEditable"/> es la regla
/// de Quotations (spec 2026-11-10 §3.6: abierto, sin preparar y sin facturar), la que decide si el pedido admite la parte.
/// </summary>
public sealed record ShopifyOrderCityState(
    string Status, bool IsStorePickup, bool HasShippingParty, bool NotesHaveRawAddress, bool IsEditable);

/// <summary>
/// Spec 2026-10-26 §4.4 y §4.6: lo que el trabajo de corrección escribe en Quotations y en Customers. Adaptador en Bootstrapper sobre
/// <c>IExternalOrderWriter</c> e <c>ICustomerProvisioning</c>, <b>un scope propio por llamada</b> (como <c>ShopifyOrderWriter</c>): cada
/// llamada confirma lo suyo y no se suma a la unidad de trabajo de Shopify. Las razones por las que no se puede vuelven en el
/// resultado; la concurrencia (<c>concurrency.conflict</c>) y los errores transitorios se propagan y el trabajo reintenta.
/// </summary>
public interface IShopifyCityCorrectionWriter
{
    /// <summary>Pone la parte de envío que el pedido no tenía y quita de las notas las líneas que empiezan por
    /// <paramref name="notesLinePrefix"/>. <paramref name="requestedCityIds"/> como en <see cref="CompleteCustomerAsync"/>: la
    /// misma parte en una ciudad que este trabajo no pidió la puso una persona.</summary>
    Task<ShopifyCityWriteResult> AttachShippingAsync(
        Guid tenantId,
        Guid orderId,
        ShopifyPartyDraft party,
        string notesLinePrefix,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Lleva la parte de envío a la ciudad nueva sólo si sigue siendo <paramref name="expected"/>;
    /// <paramref name="requestedCityIds"/> como en <see cref="AttachShippingAsync"/>.</summary>
    Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
        Guid tenantId,
        Guid orderId,
        ShopifyShippingFingerprint expected,
        Guid departmentId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Completa al cliente que la importación creó incompleto, sólo si sigue en <paramref name="expectedVersion"/>.
    /// <paramref name="requestedCityIds"/> son las ciudades que este trabajo pidió hasta ahora: con ellas Customers (y
    /// Quotations, en un pedido) reconoce su propio reintento (una ciudad que no pidió la puso una persona).</summary>
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

/// <summary>Spec 2026-11-17 §5.3: una opción del producto para Catalog, por nombre.</summary>
public sealed record ShopifyCatalogOption(string Name, IReadOnlyList<string> Values);

/// <summary>Una variante para Catalog: <see cref="Code"/> es el SKU recortado o <c>shp-{id}</c>; <see cref="Price"/> ya con la
/// regla de impuestos y redondeo; <see cref="OptionValues"/> en el orden de las opciones.</summary>
public sealed record ShopifyCatalogVariant(
    long ShopifyVariantId, string Code, string? Barcode, IReadOnlyList<string> OptionValues, decimal Price);

/// <summary>El producto como lo escribe la importación o el webhook. <see cref="TaxRateId"/> sólo se usa al crear (plan D4).
/// <see cref="ActorId"/> firma la auditoría: el usuario que pidió la importación o <see cref="Guid.Empty"/> para el sistema (P3).</summary>
public sealed record ShopifyCatalogProduct(
    Guid TenantId,
    string Name,
    bool IsActive,
    Guid? TaxRateId,
    string Currency,
    IReadOnlyList<ShopifyCatalogOption> Options,
    IReadOnlyList<ShopifyCatalogVariant> Variants,
    Guid ActorId,
    DateTimeOffset OccurredAt);

/// <summary>Lo que dejó la escritura. <see cref="RejectedCode"/>: el código de la regla de Catalog que la rechazó (P7); con él,
/// <see cref="ProductId"/> es <see cref="Guid.Empty"/>, no hay variantes y <b>la transacción ya no sirve</b> (quien llama la
/// descarta). <see cref="Changed"/> es <c>false</c> si nada cambiaba y no se guardó (la versión del producto sigue igual).</summary>
public sealed record ShopifyCatalogWrite(
    Guid ProductId,
    IReadOnlyDictionary<long, Guid> VariantIds,
    IReadOnlyList<long> DroppedBarcodes,
    bool Changed,
    string? RejectedCode)
{
    public static ShopifyCatalogWrite Rejected(string code) => new(Guid.Empty, new Dictionary<long, Guid>(), [], false, code);
}

/// <summary>Plan D4: la tasa de IVA 19 % y la Exento del tenant (la activa más antigua), o <c>null</c> si no la tiene.</summary>
public sealed record ShopifyDefaultTaxRates(Guid? Vat19Id, Guid? ExemptId);

/// <summary>
/// Spec 2026-11-17 §5.4: escribe en Catalog <b>dentro de la transacción abierta de Shopify</b> (sin ella lanza
/// <see cref="InvalidOperationException"/>). Cada llamada abre un scope hijo, enrola su <c>CatalogDbContext</c> en la conexión y la
/// transacción del <c>ShopifyDbContext</c> (precedente <c>ShopifyOrderCorrectionWriter</c>) y llama al camino de sistema de Catalog:
/// producto, opciones y vínculos se confirman juntos o no se confirman. Una regla de Catalog vuelve como
/// <see cref="ShopifyCatalogWrite.RejectedCode"/>; el resto se propaga. Adaptador en Bootstrapper.
/// </summary>
public interface IShopifyCatalogWriter
{
    Task<ShopifyCatalogWrite> CreateAsync(ShopifyCatalogProduct product, CancellationToken cancellationToken);

    /// <param name="existing">La variante de QEP de cada variante de Shopify ya vinculada. Una variante del producto a la que no
    /// apunta ninguna del borrador se borra si ninguna línea la usa y se desactiva si alguna sí, como el PUT.</param>
    Task<ShopifyCatalogWrite> UpdateAsync(
        Guid productId, ShopifyCatalogProduct product, IReadOnlyDictionary<long, Guid> existing, CancellationToken cancellationToken);

    /// <summary>Activa o desactiva. Un rechazo de Catalog sale como excepción (el producto ya existía).</summary>
    Task SetActiveAsync(Guid tenantId, Guid productId, bool active, Guid actorId, CancellationToken cancellationToken);

    /// <summary>Desvincular (§5.5): el producto queda propio de QEP. Sin efecto si ya lo era o si ya no existe.</summary>
    Task ReleaseAsync(Guid tenantId, Guid productId, Guid actorId, CancellationToken cancellationToken);

    /// <summary>Sólo lee: no necesita transacción.</summary>
    Task<ShopifyDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>La imagen descargada y su tipo (<c>image/jpeg</c>, <c>image/png</c> o <c>image/webp</c>).</summary>
public sealed record ShopifyFetchedImage(byte[] Content, string ContentType);

/// <summary>
/// Spec 2026-11-17 §5.3: baja la imagen destacada. Sólo <c>https://cdn.shopify.com</c>, sin redirecciones, 10 s, tope de
/// <see cref="MaxBytes"/> (lee hasta el tope más uno y corta). <c>null</c> con cualquier falla (quien llama anota
/// <c>image_failed</c>); nunca registra la URL con su query.
/// </summary>
public interface IShopifyImageFetcher
{
    public const long MaxBytes = 10L * 1024 * 1024;

    Task<ShopifyFetchedImage?> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Cómo terminó la imagen: <see cref="FileId"/> el archivo que quedó en la galería (<c>null</c> si no entró), o
/// <see cref="FailureCode"/> si no se pudo guardar.</summary>
public sealed record ShopifyImageAttach(Guid? FileId, string? FailureCode);

/// <summary>
/// Spec 2026-11-17 §10 P1: guarda la imagen como archivo <c>Product</c> de Storage (dueño = el producto) y la pone en la galería en
/// lugar de <paramref name="previousFileId"/> (la que puso la importación). Fuera de la transacción del producto, con un scope propio
/// por llamada; un <c>concurrency.conflict</c> de Catalog reintenta una vez. Adaptador en Bootstrapper.
/// </summary>
public interface IShopifyProductImageStore
{
    Task<ShopifyImageAttach> AttachAsync(
        Guid tenantId, Guid productId, ShopifyFetchedImage image, Guid? previousFileId, Guid actorId, CancellationToken cancellationToken);
}

/// <summary>Plan D6: la URL para volver a autorizar la app con los scopes actuales (<c>StartShopifyInstallForTenantCommand</c>).
/// Corre en el request del asesor, con su permiso.</summary>
public interface IShopifyReauthorization
{
    Task<string> CreateUrlAsync(Guid tenantId, string shopDomain, string redirectUri, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-17 §10 P8: <c>qep.shopify.product_sync.failures {reason}</c> (Prometheus
/// <c>qep_shopify_product_sync_failures_total</c>). Sin tenant ni datos del producto.</summary>
public interface IShopifyProductSyncMetrics
{
    void RecordFailure(string reason);
}
