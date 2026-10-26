using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Las tiendas (§6.1). Todo método recibe <c>tenantId</c>; lo agregado commitea con <see cref="IShopifyUnitOfWork"/>.</summary>
public interface IShopifyStoreRepository
{
    Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(ShopifyStore store);

    /// <summary>Al nacer la tienda de una conexión, pasa a <paramref name="connectionId"/> las filas <c>Waiting</c> del espejo del
    /// mismo tenant cuyas tiendas tienen el mismo <paramref name="shopDomain"/> y otra conexión: las que dejó una conexión que se
    /// borró antes de volver a enlazar la tienda (D-S23). Así la conciliación y el reintento, que trabajan con la conexión actual,
    /// las retoman. Las filas terminales no se tocan, ni una cuyo pedido ya tenga fila en la conexión nueva. Escribe en el acto
    /// (no espera al commit) y sube la versión de cada fila; repetirla no cambia nada. Devuelve cuántas filas pasaron.</summary>
    Task<int> AdoptWaitingOrdersAsync(Guid tenantId, Guid connectionId, string shopDomain, CancellationToken cancellationToken);

    /// <summary>§5.5: <c>shop/redact</c> encuentra la tienda por su dominio aunque la conexión y su ruta se hayan borrado.
    /// Sin <c>tenantId</c> a propósito: el webhook no sabe de qué tenant es la tienda. Puede devolver más de una si la
    /// tienda se enlazó a otro tenant después.</summary>
    Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken);

    /// <summary>§7 <c>GET /stores</c>: las tiendas del tenant, sin tracking.</summary>
    Task<IReadOnlyList<ShopifyStore>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>El espejo de pedidos (§5.4). Todo método recibe <c>tenantId</c>.</summary>
public interface IShopifyOrderMirrorRepository
{
    Task<ShopifyOrderMirror?> FindAsync(Guid tenantId, Guid connectionId, long shopifyOrderId, CancellationToken cancellationToken);

    void Add(ShopifyOrderMirror row);

    /// <summary>§6.2.1 paso 2: el <c>qep_customer_id</c> que el espejo de esta tienda ya anotó para ese comprador de Shopify, del
    /// pedido más reciente que lo tenga; <c>null</c> si nunca se anotó. Consulta propia del módulo (usa
    /// <c>IX_shopify_orders_connection_customer</c>).</summary>
    Task<Guid?> FindSeenCustomerIdAsync(Guid tenantId, Guid connectionId, long shopifyCustomerId, CancellationToken cancellationToken);

    /// <summary>§5.5 <c>shop/redact</c>: borra en el acto (no espera al commit) las filas de la tienda.</summary>
    Task<int> DeleteByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>§5.3 y §6.4: las filas <c>Waiting</c> de la tienda, los pedidos más viejos primero (<c>shopify_created_at</c>, luego
    /// el id), hasta <paramref name="limit"/>. Sin tracking: la conciliación las vuelve a pasar por la ingesta, que las relee.</summary>
    Task<IReadOnlyList<ShopifyWaitingOrder>> ListWaitingAsync(Guid tenantId, Guid connectionId, int limit, CancellationToken cancellationToken);

    /// <summary>§7: una fila por su id, con tracking (el reintento y «Marcar como revisado» la escriben).</summary>
    Task<ShopifyOrderMirror?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>§7 <c>GET /orders</c>: la página del espejo, los pedidos más recientes primero (<c>shopify_created_at</c> y luego
    /// el id, descendentes), sin tracking, y el total del filtro.</summary>
    Task<(IReadOnlyList<ShopifyOrderMirror> Items, int Total)> SearchAsync(
        Guid tenantId, ShopifyOrderFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>§7 <c>GET /orders/summary</c>: los cuatro contadores en una consulta. <paramref name="importedSince"/> es el
    /// comienzo del día de hoy en el huso del tenant.</summary>
    Task<ShopifyOrderCounts> CountAsync(Guid tenantId, DateTimeOffset importedSince, CancellationToken cancellationToken);

    /// <summary>§7 <c>GET /stores</c>: las filas <c>Waiting</c> de cada tienda del tenant; una tienda sin ninguna no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountWaitingByConnectionAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-26 §5: las huellas de los sinónimos (<c>shopify.city_corrections</c>). Con tracking; lo agregado commitea
/// con <see cref="IShopifyUnitOfWork"/>, que traduce el índice único <c>(sinónimo, destino, id)</c> a <c>concurrency.conflict</c>.</summary>
public interface IShopifyCityCorrectionRepository
{
    Task<ShopifyCityCorrection?> FindAsync(
        Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId, CancellationToken cancellationToken);

    /// <summary>Por id, sin tenant: la usa el trabajo de corrección, que corre fuera de un request (SC-08).</summary>
    Task<ShopifyCityCorrection?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(ShopifyCityCorrection correction);

    /// <summary>§4.6 y O8: las huellas de un sinónimo, de los dos orígenes y de <b>todos</b> los tenants (el sinónimo es de
    /// plataforma), sin tracking y en orden de id: la siembra del evento <c>corrected</c> sólo las lee. Va por
    /// <c>IX_city_corrections_synonym_target</c>.</summary>
    Task<IReadOnlyList<ShopifyCityCorrection>> ListBySynonymAsync(Guid synonymId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-26 D-C6: los trabajos de corrección (<c>shopify.city_correction_jobs</c>). Con tracking; lo agregado
/// commitea con <see cref="IShopifyUnitOfWork"/>. Sin tenant: el worker corre fuera de un request y el trabajo lleva el suyo.</summary>
public interface IShopifyCityCorrectionJobRepository
{
    Task<ShopifyCityCorrectionJob?> FindAsync(Guid id, CancellationToken cancellationToken);

    void Add(ShopifyCityCorrectionJob job);
}

/// <summary>
/// Spec 2026-10-26 D-C6: la cola del worker que corrige, por SQL. Como <see cref="IShopifyReconcileLeases"/>, cada método es una
/// sentencia que se confirma en el acto, <b>fuera</b> del tracker: el reclamo no puede esperar al guardado del trabajo (otra réplica
/// lo tomaría a la vez), y el <c>Failed</c> se escribe justo cuando el procesador lanzó y su unidad de trabajo no se guarda.
/// </summary>
// CA1711: el analizador asume que «Queue» implica heredar de System.Collections.Queue; acá es el sustantivo del plan, como
// IExportJobQueue de Quotations: falso positivo.
#pragma warning disable CA1711
public interface IShopifyCityCorrectionJobQueue
#pragma warning restore CA1711
{
    /// <summary>Los <c>Pending</c> sin lease o con el lease vencido, los más viejos primero, hasta <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Toma el trabajo (un solo ganador entre réplicas), suma un intento y le pone el lease de ese intento. Devuelve el
    /// número de intento, o <c>null</c> si ya no está <c>Pending</c> o alguien tiene el lease.</summary>
    Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Lo cierra <c>Failed</c> con <paramref name="lastError"/>, si sigue <c>Pending</c>.</summary>
    Task MarkFailedAsync(Guid id, DateTimeOffset now, string lastError, CancellationToken cancellationToken);
}

/// <summary>El filtro de la bandeja (§7). <see cref="States"/> vacío es cualquier estado («con avisos» sin
/// <c>state</c>, BFF: así la lista cierra con <c>summary.withWarnings</c>); el handler pone <c>Waiting</c> cuando no hay <c>state</c> ni <c>withWarnings</c>. <see cref="WithWarnings"/>
/// deja sólo las filas con avisos <b>sin revisar</b>.</summary>
public sealed record ShopifyOrderFilter(
    IReadOnlyCollection<ShopifyImportState> States, Guid? ConnectionId, bool WithWarnings, bool CancelledAfterInvoice = false);

/// <summary>Los contadores del resumen (§7), como los arma la base.</summary>
public sealed record ShopifyOrderCounts(int Waiting, int WithWarnings, int CancelledAfterInvoice, int ImportedToday);

/// <summary>Una fila en espera como la reintenta la conciliación: el pedido y su último snapshot normalizado (§5.4).</summary>
public sealed record ShopifyWaitingOrder(long ShopifyOrderId, string Payload);

/// <summary>
/// §5.3: el lease de la conciliación y su ventana en <c>shopify.stores</c>. Cada método es un <c>UPDATE</c> que se confirma en el
/// acto, <b>fuera</b> del tracker de la unidad de trabajo: el importador hace un <c>SaveChanges</c> a mitad de camino (D-S24) que
/// confirmaría cualquier cambio rastreado, y un lease pendiente ahí se soltaría o se tomaría cuando no toca. Tampoco sube la
/// <c>version</c> de la tienda: es el <c>If-Match</c> del <c>PUT</c> y nada de esto lo edita el usuario.
/// </summary>
public interface IShopifyReconcileLeases
{
    /// <summary>El <c>reconcile_lease_until</c> que quedó, que es la prueba de que el lease es de esta corrida; <c>null</c> si la
    /// fila no existe o si otra corrida tiene un lease vigente.</summary>
    Task<DateTimeOffset?> TryAcquireAsync(Guid tenantId, Guid connectionId, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Suelta el lease sólo si sigue siendo el que tomó esta corrida (<paramref name="leaseUntil"/>): pasado su vencimiento
    /// otra corrida pudo tomarlo, y soltárselo la dejaría solaparse con una tercera.</summary>
    Task ReleaseAsync(Guid tenantId, Guid connectionId, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    /// <summary>Mueve <c>last_reconciled_at</c>: la próxima corrida trae desde ahí menos el solape.</summary>
    Task RecordReconciledAsync(Guid tenantId, Guid connectionId, DateTimeOffset reconciledAt, CancellationToken cancellationToken);
}

/// <summary>§5.2: el cuerpo REST de un webhook <c>orders/*</c> a <see cref="ShopifyOrderSnapshot"/>. El parser vive en
/// Infrastructure (lee <c>JsonElement</c>); un cuerpo que no sabe leer lanza <see cref="ShopifyPayloadException"/>.</summary>
public interface IShopifyOrderPayloadParser
{
    ShopifyOrderSnapshot ParseRest(string payloadJson);
}
