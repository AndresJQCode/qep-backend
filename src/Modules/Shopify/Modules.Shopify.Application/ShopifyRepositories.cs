using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Las tiendas (§6.1). Todo método recibe <c>tenantId</c>; lo agregado commitea con <see cref="IShopifyUnitOfWork"/>.</summary>
public interface IShopifyStoreRepository
{
    Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(ShopifyStore store);

    /// <summary>Ruling S16: <c>shop/redact</c> encuentra la tienda por su dominio aunque la conexión y su ruta se hayan borrado.
    /// Sin <c>tenantId</c> a propósito (como S6): el webhook no sabe de qué tenant es la tienda. Puede devolver más de una si la
    /// tienda se enlazó a otro tenant después.</summary>
    Task<IReadOnlyList<ShopifyStore>> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken);
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
}

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
