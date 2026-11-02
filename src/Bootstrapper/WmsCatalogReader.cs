using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Wms, por variante (puerto <see cref="IWmsCatalogReader"/>). Vive acá y no en ninguno de los dos módulos: Wms nunca
/// referencia Catalog (<c>WmsLayerTests</c>) y el composition root es el único lugar donde ese acoplamiento es legítimo. Nombra la
/// variante con <see cref="CatalogVariantReads"/>, el mismo código de las líneas de cotización y del POS, para que el perfil
/// logístico no la llame distinto.
/// </summary>
internal sealed class WmsCatalogReader(IProductRepository products, IOptionDefinitionRepository options) : IWmsCatalogReader
{
    public async Task<IReadOnlyList<WmsVariantSnapshot>> GetVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var wanted = variantIds.ToHashSet();
        var found = await products.FindVariantsByIdsAsync(tenantId, wanted.Select(id => new VariantId(id)).ToArray(), ct);
        var selected = CatalogVariantReads.Select(found, wanted);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, found, ct);

        return selected
            .Select(pair => new WmsVariantSnapshot(
                pair.Variant.Id.Value,
                pair.Product.Id.Value,
                pair.Product.Name,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                pair.Variant.Code,
                pair.Variant.Barcode,
                pair.Variant.PackagingUnits,
                CatalogVariantReads.IsSellable(pair.Product, pair.Variant)))
            .ToArray();
    }
}
