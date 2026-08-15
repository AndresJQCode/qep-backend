namespace Modules.Catalog.Domain;

/// <summary>
/// Las cinco propiedades opcionales de un producto (CAT-04), agrupadas.
///
/// Van juntas y no como cinco parámetros sueltos de <c>Create</c>/<c>Update</c> por dos razones.
/// La menor: con los cinco sueltos la firma llega a diez argumentos. La que importa: la
/// invariante «precio y moneda van juntos» **cruza dos campos**, y suelta habría que repetirla en
/// los dos métodos o dejarla sin dueño.
///
/// **Las propiedades son `init` y no posicionales, así que sólo se construye por nombre.** Era un
/// record posicional, y el hallazgo `E` de la revisión de 4 lentes mostró el agujero:
/// `Description` y `Currency` son los dos `string?` y estaban en posiciones **no adyacentes**, de
/// modo que intercambiarlos compilaba sin una queja. Un producto quedaba con la descripción en la
/// moneda. Ahora ese error no tiene forma de escribirse.
/// </summary>
public sealed record ProductDetails
{
    public string? Description { get; init; }

    public Guid? ImageFileId { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }

    public TaxRateId? TaxRateId { get; init; }

    // Espeja los anchos de columna, igual que Name y Code en Product: un valor demasiado largo
    // falla como 422 con código de dominio en vez de llegar a PostgreSQL y volver como 500.
    public const int DescriptionMaxLength = 2000;

    // ISO-4217 alfabético: siempre tres letras.
    public const int CurrencyLength = 3;

    public static ProductDetails Empty { get; } = new();

    /// <summary>
    /// Normaliza y hace cumplir los invariantes. Lo llama <see cref="Product"/>; no es punto de
    /// entrada público, del mismo modo que <c>Product.Create</c> es el único que construye el
    /// agregado.
    /// </summary>
    internal ProductDetails Normalized()
    {
        var description = NormalizeDescription(Description);
        var currency = NormalizeCurrency(Currency);
        var price = EnsurePriceNotNegative(Price);

        // Los dos sentidos en una sola comparación. Un precio sin moneda es un número sin unidad;
        // una moneda sin precio no dice nada. Escrita en un solo sentido, la guarda deja pasar el
        // otro.
        if (price.HasValue != (currency is not null))
        {
            throw new CatalogDomainException(
                "catalog.product.price_currency_mismatch",
                "Price and currency must be provided together.");
        }

        return this with { Description = description, Price = price, Currency = currency };
    }

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

    // "cop" y "COP" son la misma moneda. Dejar las dos formas en base obliga a cada consumidor a
    // normalizar de nuevo, y basta con que uno se olvide para que la comparación falle.
    private static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var trimmed = currency.Trim();
        return trimmed.Length != CurrencyLength || !trimmed.All(char.IsLetter)
            ? throw new CatalogDomainException(
                "catalog.product.currency_invalid",
                "The currency must be a three-letter ISO 4217 code.")
            : trimmed.ToUpperInvariant();
    }

    // El cero es válido: un producto promocional puede valer 0. La guarda va contra el negativo,
    // no contra el falsy.
    private static decimal? EnsurePriceNotNegative(decimal? price) =>
        price < 0m
            ? throw new CatalogDomainException(
                "catalog.product.price_negative",
                "The product price cannot be negative.")
            : price;
}
