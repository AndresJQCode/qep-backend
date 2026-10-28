using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el catálogo al puerto que <c>quotations</c> declara para pintar sus líneas. Vive acá,
/// como el resto de los adaptadores entre módulos.
///
/// Por **variante** (spec 2026-10-20 §4): cada línea vende una variante, así que el código, el
/// nombre visible, la foto y las escalas son de ella. Trae activas e inactivas —variante o
/// producto—: una línea vieja sigue necesitando su nombre.
///
/// Pocas consultas por pantalla y no una por línea: los productos de las variantes del lote, las
/// opciones de esos productos (para el nombre visible) y, con los ids de sus fotos, los archivos.
/// Es lo que evita que el detalle de una cotización con diez líneas cueste treinta lecturas.
/// </summary>
internal sealed class QuotationProductLookup(
    IProductRepository repository,
    IOptionDefinitionRepository optionRepository,
    IProductImageLookup imageLookup)
    : IQuotationProductLookup
{
    public async Task<IReadOnlyDictionary<Guid, QuotationVariantRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationVariantRef>();
        }

        var wanted = variantIds.ToHashSet();
        var products = await repository.FindVariantsByIdsAsync(
            tenantId,
            wanted.Select(id => new VariantId(id)).ToArray(),
            cancellationToken);
        var selected = CatalogVariantReads.Select(products, wanted);

        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(
            optionRepository, tenantId, products, cancellationToken);
        var images = await CatalogVariantReads.LoadImagesAsync(
            imageLookup,
            selected.Select(pair => CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant)),
            cancellationToken);

        return selected.ToDictionary(
            pair => pair.Variant.Id.Value,
            pair => new QuotationVariantRef(
                pair.Variant.Id.Value,
                pair.Product.Id.Value,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                pair.Variant.Code,
                CatalogVariantReads.ImageUrlOf(
                    CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant), images, tenantId),
                pair.Variant.ToQuotationRefs()));
    }
}
