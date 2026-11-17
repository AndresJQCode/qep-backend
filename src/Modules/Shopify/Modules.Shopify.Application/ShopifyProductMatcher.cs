namespace Modules.Shopify.Application;

public enum ShopifyMatchKind
{
    /// <summary>Ninguna variante casa por SKU: se crea el producto.</summary>
    None,

    /// <summary>Todas casan en un mismo producto de QEP: se vincula todo, no se crea nada.</summary>
    All,

    /// <summary>Casan algunas: se vinculan esas, el producto no se crea y las demás se saltan con <c>partial_match</c>.</summary>
    Partial,

    /// <summary>P9: el producto de QEP elegido ya era importado de esta conexión (Shopify lo borró y lo recreó): se vuelve a
    /// vincular como importado y se reactiva.</summary>
    Relink,
}

/// <summary>
/// Lo que casó. <see cref="QepProductId"/> es el producto de QEP elegido (P6: el de más variantes casadas; empate, el de la primera
/// en el orden de Shopify). <see cref="Matched"/>: variante de Shopify → variante de QEP, todas de ese producto.
/// <see cref="PartialMatch"/>: las que no casaron (sin SKU incluidas). <see cref="InOtherProduct"/>: las que casaron en <b>otro</b>
/// producto de QEP (P6: nunca se vinculan).
/// </summary>
public sealed record ShopifyMatch(
    ShopifyMatchKind Kind,
    Guid? QepProductId,
    IReadOnlyDictionary<long, Guid> Matched,
    IReadOnlyList<long> PartialMatch,
    IReadOnlyList<long> InOtherProduct);

/// <summary>
/// Spec 2026-11-17 §5.3, §10 P6 y P9: el emparejamiento por SKU de un producto de Shopify sin vínculos. Una variante casa si su SKU
/// recortado es el <c>Code</c> de una variante de QEP (comparación ordinal, como el índice único); una sin SKU nunca casa por SKU
/// (su <c>shp-{id}</c> lo pone el mapeo, no Shopify). Dos variantes de Shopify con el mismo SKU vinculan sólo la primera: la otra
/// va a <see cref="ShopifyMatch.PartialMatch"/> (el índice único por variante de QEP no deja dos vínculos). Puro.
/// </summary>
public static class ShopifyProductMatcher
{
    /// <summary>Los SKU a buscar en Catalog (<c>IShopifyProductLookup.FindByCodesAsync</c>): recortados, sin vacíos ni repetidos.</summary>
    public static IReadOnlyList<string> SkusOf(ShopifyProductSnapshot product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return product.Variants
            .Select(variant => variant.Sku?.Trim())
            .Where(sku => !string.IsNullOrEmpty(sku))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <param name="byCode">Lo que devolvió <c>FindByCodesAsync</c> para <see cref="SkusOf"/>.</param>
    /// <param name="importedProducts">Los productos de QEP con algún vínculo <c>Imported</c> en esta conexión (P9).</param>
    public static ShopifyMatch Match(
        ShopifyProductSnapshot product, IReadOnlyDictionary<string, ShopifyProductRef> byCode, IReadOnlySet<Guid> importedProducts)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(byCode);
        ArgumentNullException.ThrowIfNull(importedProducts);

        var hits = new List<(long ShopifyVariantId, ShopifyProductRef Ref)>();
        foreach (var variant in product.Variants)
        {
            var sku = variant.Sku?.Trim();
            if (!string.IsNullOrEmpty(sku) && byCode.TryGetValue(sku, out var found))
            {
                hits.Add((variant.Id, found));
            }
        }

        if (hits.Count == 0)
        {
            return new ShopifyMatch(ShopifyMatchKind.None, null, new Dictionary<long, Guid>(), [], []);
        }

        // P6: el de más variantes casadas; empate, el de la primera casada en el orden de Shopify (GroupBy conserva ese orden).
        var chosen = hits
            .GroupBy(hit => hit.Ref.ProductId)
            .OrderByDescending(group => group.Count())
            .First()
            .Key;

        var matched = new Dictionary<long, Guid>();
        var usedVariants = new HashSet<Guid>();
        var inOther = new List<long>();
        foreach (var (shopifyVariantId, found) in hits)
        {
            if (found.ProductId != chosen)
            {
                inOther.Add(shopifyVariantId);
            }
            else if (usedVariants.Add(found.VariantId))
            {
                matched[shopifyVariantId] = found.VariantId;
            }
        }

        var partial = product.Variants
            .Select(variant => variant.Id)
            .Where(id => !matched.ContainsKey(id) && !inOther.Contains(id))
            .ToArray();

        var kind = importedProducts.Contains(chosen)
            ? ShopifyMatchKind.Relink
            : matched.Count == product.Variants.Count ? ShopifyMatchKind.All : ShopifyMatchKind.Partial;
        return new ShopifyMatch(kind, chosen, matched, partial, inOther);
    }
}
