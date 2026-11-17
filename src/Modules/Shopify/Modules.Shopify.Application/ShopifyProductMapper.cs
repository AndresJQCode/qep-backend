using System.Globalization;
using BuildingBlocks.Domain.Pricing;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Lo que el mapeo necesita de la tienda y del tenant: la moneda de la tienda, si sus precios incluyen impuestos, si la empresa
/// emisora cobra IVA, la regla de redondeo de esa moneda en el tenant (<c>ITenantPriceRounding</c>) y las tasas por defecto (D4).
/// </summary>
public sealed record ShopifyPricingContext(
    string Currency, bool TaxesIncluded, bool CompanyChargesVat, PriceRounding Rounding, ShopifyDefaultTaxRates Rates);

/// <summary>El producto listo para Catalog, cuántas variantes entran sin SKU y los avisos del mapeo (<c>mixed_taxable</c>,
/// <c>tax_rate_missing</c>).</summary>
public sealed record ShopifyMappedProduct(ShopifyCatalogProduct Product, int WithoutSku, IReadOnlyList<string> IssueCodes);

/// <summary>
/// Spec 2026-11-17 §5.3 y plan D4/D5: un producto de Shopify como lo escribe Catalog. Puro: no lee nada.
/// <list type="bullet">
/// <item><b>Estado:</b> <c>Active</c> es activo; <c>Draft</c> y <c>Archived</c>, inactivo (quien llama decide si trae un archivado).</item>
/// <item><b>Opciones:</b> una sola variante con la opción <c>Title</c> / <c>Default Title</c> entra sin opciones; si no, nombres y
/// valores recortados, en el orden de Shopify.</item>
/// <item><b>Código:</b> el SKU recortado, o <c>shp-{id}</c> si no tiene (cuenta en <c>without_sku</c>); barras recortado o <c>null</c>.</item>
/// <item><b>Tasa (D4, P4):</b> alguna variante <c>taxable</c> → 19 %; ninguna → Exento; mezcla → 19 % y aviso <c>mixed_taxable</c>. Se
/// asigna sin mirar si la empresa cobra IVA. Sin esa tasa en el tenant, sin tasa y aviso <c>tax_rate_missing</c>.</item>
/// <item><b>Precio (D5):</b> con <c>taxesIncluded = false</c>, empresa con IVA, moneda COP y tasa elegida, se le suma el IVA de esa tasa
/// (19 o 0) con el redondeo de la moneda; en cualquier otro caso va tal cual (misma regla que <see cref="ShopifyPricing.UnitPriceWithVat"/>).</item>
/// </list>
/// </summary>
public static class ShopifyProductMapper
{
    public const string NoSkuPrefix = "shp-";

    private const string DefaultOptionName = "Title";

    private const string DefaultOptionValue = "Default Title";

    private const int VatPercentage = 19;

    public static ShopifyMappedProduct Map(
        ShopifyProductSnapshot product, ShopifyPricingContext pricing, Guid tenantId, Guid actorId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(pricing);

        var issues = new List<string>();
        var withoutOptions = IsDefaultOnly(product);
        var options = withoutOptions
            ? []
            : product.Options.Select(option => new ShopifyCatalogOption(option.Name.Trim(), [.. option.Values.Select(value => value.Trim())])).ToArray();

        var anyTaxable = product.Variants.Any(variant => variant.Taxable);
        var allTaxable = product.Variants.All(variant => variant.Taxable);
        if (anyTaxable && !allTaxable)
        {
            issues.Add(ShopifyProductCodes.MixedTaxable);
        }

        var (taxRateId, percentage) = anyTaxable ? (pricing.Rates.Vat19Id, VatPercentage) : (pricing.Rates.ExemptId, 0);
        if (taxRateId is null)
        {
            issues.Add(ShopifyProductCodes.TaxRateMissing);
        }

        var addVat = !pricing.TaxesIncluded && pricing.CompanyChargesVat && ShopifyPricing.IsCop(pricing.Currency) && taxRateId is not null;
        var variants = product.Variants
            .Select(variant => new ShopifyCatalogVariant(
                variant.Id,
                CodeOf(variant),
                string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim(),
                withoutOptions ? [] : [.. variant.OptionValues.Select(value => value.Trim())],
                addVat ? pricing.Rounding.Apply(variant.Price * (100 + percentage) / 100m) : variant.Price))
            .ToArray();

        return new ShopifyMappedProduct(
            new ShopifyCatalogProduct(
                tenantId,
                product.Title.Trim(),
                product.Status == ShopifyProductStatus.Active,
                taxRateId,
                pricing.Currency,
                options,
                variants,
                actorId,
                now),
            product.Variants.Count(variant => string.IsNullOrWhiteSpace(variant.Sku)),
            issues);
    }

    /// <summary>El SKU recortado o <c>shp-{id}</c>: único, estable y reconocible (§5.3), cabe en los 60 del código.</summary>
    public static string CodeOf(ShopifyVariantSnapshot variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return string.IsNullOrWhiteSpace(variant.Sku)
            ? NoSkuPrefix + variant.Id.ToString(CultureInfo.InvariantCulture)
            : variant.Sku.Trim();
    }

    private static bool IsDefaultOnly(ShopifyProductSnapshot product) =>
        product.Variants.Count == 1
        && product.Options.Count == 1
        && string.Equals(product.Options[0].Name.Trim(), DefaultOptionName, StringComparison.Ordinal)
        && product.Variants[0].OptionValues is [var value]
        && string.Equals(value.Trim(), DefaultOptionValue, StringComparison.Ordinal);
}
