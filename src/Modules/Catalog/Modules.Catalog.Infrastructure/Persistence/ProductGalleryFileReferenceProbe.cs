using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog retiene un archivo mientras alguna galería, de cualquier producto, lo tenga. Storage la
/// consulta antes de purgar una foto liberada (<c>catalog.product.images-released.v1</c>): la misma
/// imagen puede estar en la galería de otro producto, y ese otro producto la sigue usando. La
/// consulta usa el índice <c>IX_product_gallery_images_file</c>.
/// </summary>
internal sealed class ProductGalleryFileReferenceProbe(CatalogDbContext dbContext)
    : IFileReferenceProbe
{
    public string Source => "catalog";

    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        dbContext.Set<ProductGalleryImage>().AnyAsync(image => image.FileId == fileId, cancellationToken);
}
