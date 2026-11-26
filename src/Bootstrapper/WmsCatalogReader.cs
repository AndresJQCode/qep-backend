using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Wms, por variante (puerto <see cref="IWmsCatalogReader"/>). Vive acá y no en ninguno de los dos módulos: Wms nunca
/// referencia Catalog (<c>WmsLayerTests</c>) y el composition root es el único lugar donde ese acoplamiento es legítimo. Nombra la
/// variante con <see cref="CatalogVariantReads"/>, el mismo código de las líneas de cotización y del POS, para que el perfil
/// logístico no la llame distinto.
/// </summary>
internal sealed class WmsCatalogReader(IProductRepository products, IOptionDefinitionRepository options, CatalogDbContext catalog)
    : IWmsCatalogReader
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

    // Los ids salen de SQL y los snapshots de GetVariantsAsync: IProductRepository.FindVariantByCodeAsync es igualdad exacta y no sirve
    // para ver que «abc» y «ABC» son dos variantes distintas con el mismo código escrito. Sirve la carga inicial por Excel, que rechaza
    // la ambigüedad en vez de elegir una.
    public async Task<IReadOnlyList<WmsVariantSnapshot>> FindVariantsByCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var upperCodes = codes.Select(code => code.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var ids = await catalog.Database.SqlQuery<Guid>($"""
            SELECT v.id AS "Value"
            FROM catalog.product_variants v
            JOIN catalog.products p ON p.id = v.product_id
            WHERE p.tenant_id = {tenantId} AND upper(v.code) = ANY({upperCodes})
            """).ToListAsync(ct);

        return await GetVariantsAsync(tenantId, ids, ct);
    }
}
