namespace Modules.Catalog.Domain;

/// <summary>
/// Una foto de la galería de un producto (hasta <see cref="Product.MaxGalleryImages"/>). La de
/// <see cref="Position"/> 0 es la imagen principal (<see cref="Product.ImageFileId"/>), y cada
/// variante puede elegir una (<see cref="ProductVariant.GalleryImageId"/>).
///
/// <see cref="FileId"/> es referencia blanda a Storage, sin FK, por la misma razón que lo era
/// <c>Product.ImageFileId</c>: <c>catalog</c> no lee las tablas de <c>storage</c>. Que el archivo
/// sea del tenant lo comprueba el caso de uso (<c>ProductImageResolver</c>).
/// </summary>
public sealed class ProductGalleryImage
{
    // EF Core materializa por acá.
    private ProductGalleryImage() { }

    internal ProductGalleryImage(Guid id, Guid fileId, int position)
    {
        Id = id;
        FileId = fileId;
        Position = position;
    }

    public Guid Id { get; private set; }

    public Guid FileId { get; private set; }

    /// <summary>Base 0; define el orden de la galería.</summary>
    public int Position { get; internal set; }
}
