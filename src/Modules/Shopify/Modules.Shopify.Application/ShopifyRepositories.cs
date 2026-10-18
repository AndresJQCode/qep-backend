using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Las tiendas (§6.1). Todo método recibe <c>tenantId</c>; lo agregado commitea con <see cref="IShopifyUnitOfWork"/>.</summary>
public interface IShopifyStoreRepository
{
    Task<ShopifyStore?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(ShopifyStore store);
}

/// <summary>El espejo de pedidos (§5.4). Todo método recibe <c>tenantId</c>.</summary>
public interface IShopifyOrderMirrorRepository
{
    Task<ShopifyOrderMirror?> FindAsync(Guid tenantId, Guid connectionId, long shopifyOrderId, CancellationToken cancellationToken);

    void Add(ShopifyOrderMirror row);

    /// <summary>§5.5 <c>shop/redact</c>: borra en el acto (no espera al commit) las filas de la tienda.</summary>
    Task<int> DeleteByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>§5.2: el cuerpo REST de un webhook <c>orders/*</c> a <see cref="ShopifyOrderSnapshot"/>. El parser vive en
/// Infrastructure (lee <c>JsonElement</c>); un cuerpo que no sabe leer lanza <see cref="ShopifyPayloadException"/>.</summary>
public interface IShopifyOrderPayloadParser
{
    ShopifyOrderSnapshot ParseRest(string payloadJson);
}
