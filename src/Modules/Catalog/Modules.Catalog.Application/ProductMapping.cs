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
        product.Currency,
        product.TaxRateId?.Value,
        product.PriceBaseUsd,
        product.PriceBaseCop,
        product.PriceFinalUsd,
        product.PriceFinalCop,
        product.Discount,
        product.PriceScales.Select(ToResponse).ToArray(),
        product.CreatedAt,
        product.UpdatedAt);

    private static PriceScaleResponse ToResponse(PriceScale scale) => new(
        scale.Id.Value,
        scale.FromUnit,
        scale.ToUnit,
        scale.Discount,
        scale.Restriction.ToWireValue(),
        scale.Multiple,
        scale.PackagingUnit,
        scale.FinalUsd,
        scale.FinalCop);

    /// <summary>
    /// Mapea una colección resolviendo las URLs en **una sola** consulta al puerto.
    ///
    /// Es la razón de ser de `CAT-05b`: sin esto, el cliente que pinta una grilla de 20 productos
    /// tiene que pedir 20 URLs de descarga, una por producto. Los productos sin portada no entran
    /// al lote, así que un catálogo sin imágenes no paga nada.
    ///
    /// **Recibe el `tenantId` y vuelve a comprobarlo, aunque el resolver ya lo haya hecho en la
    /// escritura.** No es redundancia: `CAT-05a` cerró la escritura, pero **no borra lo que ya
    /// estaba**. Cualquier producto cargado antes de este slice pudo guardar el `imageFileId` de
    /// otro tenant, porque nadie lo verificaba, y esa fila sigue en la base. Sin esta
    /// comprobación, la escritura queda cerrada y la lectura abierta — y la lectura es la que se
    /// pinta en una grilla. Lo encontró la revisión, y tiene su prueba.
    /// </summary>
    public static async Task<IReadOnlyList<ProductDto>> ToDtosAsync(
        this IEnumerable<Product> products,
        IProductImageLookup imageLookup,
        Guid tenantId,
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
            .Select(product => product.ToDto(UrlOf(product, images, tenantId)))
            .ToArray();
    }

    // Un archivo que desapareció de Storage, que nunca se publicó, o que es de otro tenant, deja
    // el producto sin URL — no sin producto. `imageFileId` sigue viajando: el cliente lo necesita
    // para el PUT, y ocultárselo le rompería la edición sin decirle por qué.
    private static string? UrlOf(
        Product product,
        IReadOnlyDictionary<Guid, ProductImageRef> images,
        Guid tenantId) =>
        product.ImageFileId is { } fileId &&
        images.TryGetValue(fileId, out var image) &&
        image.TenantId == tenantId
            ? image.PublicUrl
            : null;
}
