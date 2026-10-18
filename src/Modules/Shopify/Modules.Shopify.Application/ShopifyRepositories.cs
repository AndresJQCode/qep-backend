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
}

/// <summary>§5.2: el cuerpo REST de un webhook <c>orders/*</c> a <see cref="ShopifyOrderSnapshot"/>. El parser vive en
/// Infrastructure (lee <c>JsonElement</c>); un cuerpo que no sabe leer lanza <see cref="ShopifyPayloadException"/>.</summary>
public interface IShopifyOrderPayloadParser
{
    ShopifyOrderSnapshot ParseRest(string payloadJson);
}
