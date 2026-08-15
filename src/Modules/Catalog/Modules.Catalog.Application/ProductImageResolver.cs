using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Resuelve el <c>imageFileId</c> que llega en un comando contra los archivos **del tenant del
/// producto**.
///
/// Existe por la misma razón que <see cref="ProductTaxRateResolver"/>, y con un agravante.
/// `TaxRateId` al menos tiene una foreign key de base que garantiza que la fila exista;
/// `ImageFileId` es referencia **blanda, sin FK** —no puede tenerla, porque cruzaría a otro
/// módulo—, así que **no hay ninguna red debajo de esta comprobación**. Sin ella, un producto del
/// tenant A puede apuntar al archivo del tenant B y la respuesta es un 201 perfectamente normal.
///
/// La cubre `CA-CAT-05-01`.
/// </summary>
internal static class ProductImageResolver
{
    /// <summary>
    /// Devuelve el archivo entero y no sólo su id: quien escribe ya pagó la consulta, y con la
    /// referencia en la mano puede armar la respuesta con su <c>PublicUrl</c> sin volver a
    /// preguntar. Es lo que evita que un `POST` cueste dos lecturas de `Storage`.
    /// </summary>
    public static async Task<ProductImageRef?> ResolveAsync(
        IProductImageLookup lookup,
        Guid tenantId,
        Guid? imageFileId,
        CancellationToken cancellationToken)
    {
        // Sin imagen no se le pregunta nada al puerto: el campo es opcional y una consulta de más
        // por cada producto sin portada se paga en todas las escrituras.
        if (imageFileId is null)
        {
            return null;
        }

        var image = await lookup.FindAsync(imageFileId.Value, cancellationToken);

        // Las dos condiciones dan el mismo código a propósito: distinguir "no existe" de "es de
        // otro tenant" le confirma al llamador que el id existe en otro tenant, que es justo lo
        // que la frontera esconde. Mismo razonamiento que ProductTaxRateResolver.
        if (image is null || image.TenantId != tenantId)
        {
            throw new CatalogDomainException(
                "catalog.product.image_not_found",
                "The image was not found in this tenant.");
        }

        // Una portada que todavía no terminó de subirse —o que quedó en cuarentena— no es una
        // portada: el producto quedaría mostrando un hueco.
        if (!image.IsAvailable)
        {
            throw new CatalogDomainException(
                "catalog.product.image_not_available",
                "The image has not finished uploading yet.");
        }

        // La regla es de catalog y se decide por el mime, no por la lista blanca de
        // FileUploadPolicy: esa lista es de storage y mezcla PDF y Office con las imágenes.
        // Preguntarle a storage "¿esto es una imagen?" sería meter una regla de catálogo en el
        // otro módulo.
        return !image.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? throw new CatalogDomainException(
                "catalog.product.image_not_an_image",
                "The file assigned as the product image is not an image.")
            : image;
    }
}
