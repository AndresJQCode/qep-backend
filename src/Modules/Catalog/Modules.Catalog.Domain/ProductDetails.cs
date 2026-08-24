namespace Modules.Catalog.Domain;

/// <summary>
/// Las propiedades opcionales de un producto (CAT-04), agrupadas.
///
/// Van juntas y no como parámetros sueltos de <c>Create</c>/<c>Update</c>: agrupar por
/// concern es el mismo criterio que separó <c>ProductPricing</c> (CAT-09) de este record —
/// <c>Price</c> vivía acá y se retiró ahí, reemplazado por el precio en USD/COP.
///
/// **Las propiedades son `init` y no posicionales, así que sólo se construye por nombre.**
/// </summary>
public sealed record ProductDetails
{
    public string? Description { get; init; }

    public Guid? ImageFileId { get; init; }

    public TaxRateId? TaxRateId { get; init; }

    // Espeja el ancho de columna, igual que Name y Code en Product: un valor demasiado largo
    // falla como 422 con código de dominio en vez de llegar a PostgreSQL y volver como 500.
    public const int DescriptionMaxLength = 2000;

    public static ProductDetails Empty { get; } = new();

    /// <summary>
    /// Normaliza y hace cumplir los invariantes. Lo llama <see cref="Product"/>; no es punto de
    /// entrada público, del mismo modo que <c>Product.Create</c> es el único que construye el
    /// agregado.
    /// </summary>
    internal ProductDetails Normalized() => this with
    {
        Description = NormalizeDescription(Description)
    };

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();
        return trimmed.Length > DescriptionMaxLength
            ? throw new CatalogDomainException(
                "catalog.product.description_too_long",
                $"The product description cannot exceed {DescriptionMaxLength} characters.")
            : trimmed;
    }
}
