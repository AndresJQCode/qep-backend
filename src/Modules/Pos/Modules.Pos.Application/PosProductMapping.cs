namespace Modules.Pos.Application;

internal static class PosProductMapping
{
    public const string Inactive = "Inactive";
    public const string PriceMissing = "PriceMissing";
    public const string NotFound = "NotFound";

    /// <summary>
    /// The price in the session currency, never a fallback to another one: selling an EUR session at
    /// the peso price would be a silent 4000x error, and there is no conversion anywhere (spec).
    /// </summary>
    internal static decimal? PriceIn(this PosProductRef product, string currency) =>
        product.Prices.TryGetValue(currency, out var price) ? price : null;

    public static string? UnsellableReason(PosProductRef product, string currency) =>
        !product.IsActive ? Inactive
        : product.PriceIn(currency) is null ? PriceMissing
        : null;

    /// <summary>
    /// La tasa es la que cobra la caja (spec 2026-11-17 R4), la misma de la vista previa: el carrito toma de acá su
    /// <c>expectedTaxPercentage</c>, y con la del producto cada línea de una caja sin IVA nacería distinta de la vista
    /// previa y el cajero vería un aviso de «el IVA cambió» en cada producto.
    /// </summary>
    public static PosProductResponse ToResponse(PosProductRef product, PosSellingContext selling)
    {
        var reason = UnsellableReason(product, selling.Currency);
        return new PosProductResponse(
            product.VariantId, product.ProductId, product.Code, product.Barcode, product.Name,
            product.PriceIn(selling.Currency), selling.EffectiveTaxPercentage(product.TaxPercentage), product.ImageUrl,
            reason is null, reason);
    }

    public static PosCatalogProductResponse ToResponse(PosCatalogProductRef product, PosSellingContext selling) =>
        new(
            product.ProductId,
            product.Name,
            product.HasOptions,
            product.ImageUrl,
            product.Variants.Select(variant => ToResponse(variant, selling)).ToArray());
}
