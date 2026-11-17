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
/// <para>Spec 2026-11-17 R7: <c>chargesVat</c> es el IVA de la empresa (en la importación) o el congelado del pedido (en el
/// planificador); en <c>false</c> el precio de Shopify no suma IVA.</para>
/// <para>Spec 2026-11-17 §4: una línea que no es personalizada y trae <see cref="ShopifyLine.VariantId"/> busca primero el vínculo
/// de la tienda (<c>shopify.product_links</c>). Con vínculo decide la variante de QEP vinculada: si está inactiva la línea queda
/// <c>product_unmatched</c> con su SKU, <b>sin</b> caer al SKU (otra variante con ese código no es la que Shopify vendió). Un vínculo
/// cuya variante ya no existe en Catalog (producto borrado a mano) cuenta como ausente y la línea va por SKU. Una consulta de
/// vínculos por pedido.</para>
/// </summary>
public sealed class ShopifyLineResolver(
    IShopifyProductLookup products, IShopifyCustomLineMappingRepository customLines, IShopifyProductLinkRepository links)
{
    public async Task<(IReadOnlyList<ShopifyOrderLineDraft> Lines, IReadOnlyList<string?> UnmatchedSkus, IReadOnlyList<string> UnmatchedTitles)> ResolveAsync(
        Guid tenantId, Guid connectionId, ShopifyOrderSnapshot snapshot, string currency, bool chargesVat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Personalizada con nombre: el título normalizado, o null si la línea va por SKU.
        static string? CustomTitleOf(ShopifyLine line) => line.IsCustom && string.IsNullOrWhiteSpace(line.Sku) ? ShopifyCustomLineMapping.NormalizeTitle(line.Title) : null;

        var current = snapshot.Lines.Where(line => !line.IsRemoved).ToArray();

        // Primero los vínculos por id de variante de Shopify, en una sola consulta.
        var shopifyVariantIds = current
            .Where(line => CustomTitleOf(line) is null && line.VariantId is not null)
            .Select(line => line.VariantId!.Value)
            .Distinct()
            .ToArray();
        var linked = shopifyVariantIds.Length == 0
            ? new Dictionary<long, ShopifyProductLink>()
            : await links.FindByShopifyVariantsAsync(tenantId, connectionId, shopifyVariantIds, cancellationToken);
        var linkedVariants = linked.Count == 0
            ? new Dictionary<Guid, ShopifyProductRef>()
            : await products.FindByVariantIdsAsync(tenantId, linked.Values.Select(link => link.VariantId).Distinct().ToArray(), cancellationToken);

        // La variante de QEP vinculada a la línea, si el vínculo existe y su variante también.
        ShopifyProductRef? LinkedOf(ShopifyLine line) =>
            CustomTitleOf(line) is null
            && line.VariantId is { } variantId
            && linked.TryGetValue(variantId, out var link)
            && linkedVariants.TryGetValue(link.VariantId, out var product)
                ? product
                : null;

        var skus = current
            .Where(line => CustomTitleOf(line) is null && LinkedOf(line) is null)
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
            else if (LinkedOf(line) is { } linkedProduct)
            {
                product = linkedProduct;
                if (!product.IsActive)
                {
                    var listed = line.Sku?.Trim() is { Length: > 0 } sku ? sku : null;
                    if (!unmatchedSkus.Contains(listed))
                    {
                        unmatchedSkus.Add(listed);
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
                // Sin IVA el precio de Shopify va tal cual, traiga o no impuestos; la línea lleva igual la tasa del producto, que
                // el pedido guarda como suya y aplica en 0.
                ShopifyPricing.UnitPriceWithVat(
                    line.OriginalUnitPrice, snapshot.TaxesIncluded, chargesVat ? product.TaxPercentage : 0, currency),
                // D2: Shopify reparte el descuento sobre la línea original; el porcentaje sale de la cantidad original.
                ShopifyPricing.DiscountPercentage(line.DiscountAmount, line.OriginalUnitPrice, line.OriginalQuantity ?? line.Quantity),
                product.TaxPercentage));
        }

        return (lines, unmatchedSkus, unmatchedTitles);
    }
}
