using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el catálogo al puerto que <c>quotations</c> declara para pintar sus líneas. Vive acá,
/// como el resto de los adaptadores entre módulos.
///
/// Dos consultas por pantalla y no una por línea: los productos de un lote y, con los ids de sus
/// portadas, los archivos de otro. Es lo que evita que el detalle de una cotización con diez
/// líneas cueste veinte lecturas.
/// </summary>
internal sealed class QuotationProductLookup(
    IProductRepository repository,
    IProductImageLookup imageLookup)
    : IQuotationProductLookup
{
    public async Task<IReadOnlyDictionary<Guid, QuotationProductRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationProductRef>();
        }

        var products = await repository.ListByIdsAsync(
            tenantId,
            productIds.Distinct().Select(id => new ProductId(id)).ToArray(),
            cancellationToken);

        var imageIds = products
            .Select(product => product.ImageFileId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var images = imageIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await imageLookup.FindManyAsync(imageIds, cancellationToken);

        return products.ToDictionary(
            product => product.Id.Value,
            product => new QuotationProductRef(
                product.Id.Value,
                product.Name,
                product.Code,
                ResolveImageUrl(product, images, tenantId),
                product.PriceScales
                    .Select(scale => new QuotationPriceScaleRef(
                        scale.FromUnit, scale.ToUnit, scale.Discount))
                    .ToArray()));
    }

    // La portada de otro tenant no se muestra ni se rechaza: se ignora. Acá no hay un request
    // que corregir —es una lectura de algo ya guardado— y esconder la referencia ajena es lo
    // que la frontera de tenant existe para hacer.
    private static string? ResolveImageUrl(
        Product product,
        IReadOnlyDictionary<Guid, ProductImageRef> images,
        Guid tenantId)
    {
        if (product.ImageFileId is not { } fileId) return null;
        if (!images.TryGetValue(fileId, out var image)) return null;

        return image.TenantId == tenantId && image.IsAvailable ? image.PublicUrl : null;
    }
}
