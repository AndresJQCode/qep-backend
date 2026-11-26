namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-11-17 §3: de dónde salió el vínculo. Se guarda por nombre.</summary>
public enum ShopifyProductLinkOrigin
{
    /// <summary>QEP creó el producto desde Shopify: manda Shopify (nombre, opciones, variantes, precios).</summary>
    Imported,

    /// <summary>El producto ya existía en QEP y se casó por SKU: nunca se pisa (S-2).</summary>
    MatchedBySku,
}

/// <summary>
/// Spec 2026-11-17 §3: una variante de Shopify vinculada a una variante de Catalog, una fila por <c>(connection_id,
/// shopify_variant_id)</c> y otra regla única por <c>(connection_id, variant_id)</c>. El producto y la variante de QEP son
/// referencias blandas (sin FK): el módulo no ve Catalog, y un producto borrado a mano deja los vínculos colgando hasta que el
/// siguiente webhook o importación los reemplaza.
/// </summary>
public sealed class ShopifyProductLink
{
    private ShopifyProductLink()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public long ShopifyProductId { get; private set; }

    public long ShopifyVariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    public ShopifyProductLinkOrigin Origin { get; private set; }

    /// <summary>§6, orden: el <c>updatedAt</c> del producto de Shopify que se aplicó por última vez. Un snapshot más viejo no pisa.</summary>
    public DateTimeOffset? ShopifyUpdatedAt { get; private set; }

    /// <summary>Plan D2: el id de la imagen destacada que se bajó (para no bajarla otra vez si no cambió).</summary>
    public long? ShopifyImageId { get; private set; }

    /// <summary>Plan D2: el archivo de Storage que puso la importación en la galería; sólo ese se reemplaza.</summary>
    public Guid? ImageFileId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ShopifyProductLink Create(
        Guid tenantId,
        Guid connectionId,
        long shopifyProductId,
        long shopifyVariantId,
        Guid productId,
        Guid variantId,
        ShopifyProductLinkOrigin origin,
        DateTimeOffset? shopifyUpdatedAt,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(productId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(variantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shopifyProductId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shopifyVariantId);
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown link origin.");
        }

        return new ShopifyProductLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ShopifyProductId = shopifyProductId,
            ShopifyVariantId = shopifyVariantId,
            ProductId = productId,
            VariantId = variantId,
            Origin = origin,
            ShopifyUpdatedAt = shopifyUpdatedAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Anota la versión de Shopify que se aplicó.</summary>
    public void RecordSync(DateTimeOffset shopifyUpdatedAt, DateTimeOffset now)
    {
        ShopifyUpdatedAt = shopifyUpdatedAt;
        UpdatedAt = now;
    }

    /// <summary>Anota la imagen que se bajó y el archivo que quedó en la galería (o <c>null</c> si no entró).</summary>
    public void RecordImage(long? shopifyImageId, Guid? fileId, DateTimeOffset now)
    {
        ShopifyImageId = shopifyImageId;
        ImageFileId = fileId;
        UpdatedAt = now;
    }
}
