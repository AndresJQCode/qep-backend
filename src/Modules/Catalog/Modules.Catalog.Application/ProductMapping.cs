using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

internal static class ProductMapping
{
    /// <summary>
    /// La URL entra como parámetro y no se saca del agregado porque **no es del agregado**:
    /// `Product` guarda cuál es su portada, y dónde se sirve ese archivo es de `Storage`. Quien
    /// llama ya la tiene —el que escribe, del resolver; el que lee, del lote— y pasarla acá evita
    /// que este mapeo necesite un puerto.
    /// </summary>
    public static ProductDto ToDto(this Product product, string? imageUrl = null) => new(
        product.Id.Value,
        product.Name,
        product.Code,
        product.IsActive,
        product.Description,
        product.ImageFileId,
        imageUrl,
        product.Price,
        product.Currency,
        product.TaxRateId?.Value,
        product.CreatedAt,
        product.UpdatedAt);

    /// <summary>
    /// Mapea una colección resolviendo las URLs en **una sola** consulta al puerto.
    ///
    /// Es la razón de ser de `CAT-05b`: sin esto, el cliente que pinta una grilla de 20 productos
    /// tiene que pedir 20 URLs de descarga, una por producto. Los productos sin portada no entran
    /// al lote, así que un catálogo sin imágenes no paga nada.
    /// </summary>
    public static async Task<IReadOnlyList<ProductDto>> ToDtosAsync(
        this IEnumerable<Product> products,
        IProductImageLookup imageLookup,
        CancellationToken cancellationToken)
    {
        var materialized = products as IReadOnlyList<Product> ?? products.ToArray();

        var imageIds = materialized
            .Select(product => product.ImageFileId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        if (imageIds.Length == 0)
        {
            return materialized.Select(product => product.ToDto()).ToArray();
        }

        var images = await imageLookup.FindManyAsync(imageIds, cancellationToken);

        return materialized
            .Select(product => product.ToDto(UrlOf(product, images)))
            .ToArray();
    }

    // Un archivo que desapareció de Storage —o que nunca se publicó— deja el producto sin URL,
    // no sin producto. `imageFileId` sigue viajando: el cliente lo necesita para el PUT.
    private static string? UrlOf(
        Product product,
        IReadOnlyDictionary<Guid, ProductImageRef> images) =>
        product.ImageFileId is { } fileId && images.TryGetValue(fileId, out var image)
            ? image.PublicUrl
            : null;
}
