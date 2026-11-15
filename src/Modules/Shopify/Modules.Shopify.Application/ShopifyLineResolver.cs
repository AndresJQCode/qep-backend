using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// §6.2.2: cada SKU contra el <c>Code</c> de una variante activa del tenant (y de un producto activo). Se resuelven <b>todas</b> las
/// líneas antes de decidir, para que <c>wait_detail</c> traiga la lista completa. Una línea sin SKU entra a <c>skus</c> como
/// <c>null</c>: es lo que trajo, y el frontend la muestra como «línea sin SKU». El SKU se compara recortado (los espacios de la tienda
/// no son parte del código).
/// <para>Spec 2026-11-13: una línea <b>personalizada</b> (<see cref="ShopifyLine.IsCustom"/>: Shopify manda <c>variant</c> y
/// <c>product</c> en <c>null</c> explícito, D1) con título casa por ese nombre recortado y exacto (ordinal) contra las asociaciones
/// <b>de la conexión</b>, y por el id de la variante asociada contra Catalog. Sin asociación, o con una variante inexistente o
/// inactiva, su título va a <c>customTitles</c> (sin repetir, en orden de aparición). Una personalizada sin título cae al camino del
/// SKU (<c>null</c>). Una línea que no es personalizada <b>nunca</b> mira las asociaciones.</para>
/// <para>Spec 2026-11-14 (D2): una línea que una edición de Shopify dejó en 0 (<see cref="ShopifyLine.IsRemoved"/>) ya no es del
/// pedido; no entra ni hace esperar. La usan la importación y el planificador de cambios, con la misma regla.</para>
/// </summary>
public sealed class ShopifyLineResolver(IShopifyProductLookup products, IShopifyCustomLineMappingRepository customLines)
{
    public async Task<(IReadOnlyList<ShopifyOrderLineDraft> Lines, IReadOnlyList<string?> UnmatchedSkus, IReadOnlyList<string> UnmatchedTitles)> ResolveAsync(
        Guid tenantId, Guid connectionId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Personalizada con nombre: el título normalizado, o null si la línea va por SKU.
        static string? CustomTitleOf(ShopifyLine line) => line.IsCustom && string.IsNullOrWhiteSpace(line.Sku) ? ShopifyCustomLineMapping.NormalizeTitle(line.Title) : null;

        var current = snapshot.Lines.Where(line => !line.IsRemoved).ToArray();
        var skus = current
            .Where(line => CustomTitleOf(line) is null)
            .Select(line => line.Sku?.Trim())
            .Where(sku => !string.IsNullOrEmpty(sku))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var found = skus.Length == 0
            ? new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal)
            : await products.FindByCodesAsync(tenantId, skus, cancellationToken);

        var customTitles = current
            .Select(CustomTitleOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mappedVariants = customTitles.Length == 0
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : await customLines.FindVariantIdsByTitlesAsync(tenantId, connectionId, customTitles, cancellationToken);
        var variantIds = mappedVariants.Values.Distinct().ToArray();
        var variants = variantIds.Length == 0
            ? new Dictionary<Guid, ShopifyProductRef>()
            : await products.FindByVariantIdsAsync(tenantId, variantIds, cancellationToken);

        var unmatchedSkus = new List<string?>();
        var unmatchedTitles = new List<string>();
        var lines = new List<ShopifyOrderLineDraft>(current.Length);
        foreach (var line in current)
        {
            ShopifyProductRef? product;
            if (CustomTitleOf(line) is { } title)
            {
                product = mappedVariants.TryGetValue(title, out var variantId) && variants.TryGetValue(variantId, out var mapped) && mapped.IsActive
                    ? mapped
                    : null;
                if (product is null)
                {
                    if (!unmatchedTitles.Contains(title, StringComparer.Ordinal))
                    {
                        unmatchedTitles.Add(title);
                    }

                    continue;
                }
            }
            else
            {
                var sku = line.Sku?.Trim();
                if (string.IsNullOrEmpty(sku) || !found.TryGetValue(sku, out product) || !product.IsActive)
                {
                    var listed = string.IsNullOrEmpty(sku) ? null : sku;
                    if (!unmatchedSkus.Contains(listed))
                    {
                        unmatchedSkus.Add(listed);
                    }

                    continue;
                }
            }

            lines.Add(new ShopifyOrderLineDraft(
                product.VariantId,
                product.ProductId,
                line.Quantity,
                ShopifyPricing.UnitPriceWithVat(line.OriginalUnitPrice, snapshot.TaxesIncluded, product.TaxPercentage, currency),
                // D2: Shopify reparte el descuento sobre la línea original; el porcentaje sale de la cantidad original.
                ShopifyPricing.DiscountPercentage(line.DiscountAmount, line.OriginalUnitPrice, line.OriginalQuantity ?? line.Quantity),
                product.TaxPercentage));
        }

        return (lines, unmatchedSkus, unmatchedTitles);
    }
}
