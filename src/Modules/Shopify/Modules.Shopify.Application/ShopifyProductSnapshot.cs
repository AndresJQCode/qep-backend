namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-17 §5.3: el estado del producto en Shopify. <c>Archived</c> no se trae.</summary>
public enum ShopifyProductStatus
{
    Active,
    Draft,
    Archived,
}

/// <summary>Una opción del producto con sus valores, en el orden de Shopify.</summary>
public sealed record ShopifyProductOption(string Name, IReadOnlyList<string> Values);

/// <summary>Una variante. <see cref="OptionValues"/> va en el orden de <see cref="ShopifyProductSnapshot.Options"/>;
/// <see cref="Price"/> en la moneda de la tienda.</summary>
public sealed record ShopifyVariantSnapshot(
    long Id, string? Sku, string? Barcode, decimal Price, bool Taxable, IReadOnlyList<string> OptionValues);

/// <summary>La imagen destacada: el id numérico del <c>MediaImage</c> (plan D1: el mismo espacio para webhook e importación) y la
/// URL de la transformación JPG de hasta 2048×2048.</summary>
public sealed record ShopifyProductImage(long Id, Uri Url);

/// <summary>Un producto de Shopify como lo lee la Admin API GraphQL, con todas sus variantes (las que no caben en una página se
/// unen antes de parsear).</summary>
public sealed record ShopifyProductSnapshot(
    long Id,
    string Title,
    ShopifyProductStatus Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ShopifyProductOption> Options,
    IReadOnlyList<ShopifyVariantSnapshot> Variants,
    ShopifyProductImage? FeaturedImage);

/// <summary>Lo que la importación necesita de la tienda: la moneda, si los precios incluyen impuestos y cuántos productos
/// activos o borradores tiene (el <c>total</c> de la importación).</summary>
public sealed record ShopifyShopInfo(string CurrencyCode, bool TaxesIncluded, int ProductsCount);

/// <summary>Una página de productos; <see cref="NextCursor"/> nulo en la última.</summary>
public sealed record ShopifyProductsPage(IReadOnlyList<ShopifyProductSnapshot> Products, string? NextCursor);

/// <summary>Lo mínimo de una variante para la cobertura: su id, su SKU y el título de su producto.</summary>
public sealed record ShopifyVariantKey(long VariantId, string? Sku, string ProductTitle);

public sealed record ShopifyVariantKeysPage(IReadOnlyList<ShopifyVariantKey> Variants, string? NextCursor);

/// <summary>Plan D1: del webhook REST sólo se leen el id y el <c>updated_at</c> (orden y <c>delete</c>); el producto se relee por
/// GraphQL. <see cref="UpdatedAt"/> nulo si el payload no lo trae (<c>products/delete</c>).</summary>
public sealed record ShopifyProductWebhookRef(long Id, DateTimeOffset? UpdatedAt);
