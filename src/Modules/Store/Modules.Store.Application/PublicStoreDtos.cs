using Modules.Store.Domain;

namespace Modules.Store.Application;

// Las formas del contrato público. Son records propios y a propósito más chicos que los del backoffice: nunca viajan costos, pisos,
// la escala global, ids de cuentas de cobro, el código ni el código de barras de una variante, ni el id del tenant.

/// <summary>El formato regional del tenant, el mismo de <c>/auth/me</c>.</summary>
public sealed record PublicRegionalFormatDto(
    string TimeZone, string DateFormat, string NumberFormat, string DefaultCurrency, string Country);

public sealed record PublicBannerDto(string? ImageUrl, string? Text);

/// <summary>
/// El contacto como lo pinta la tienda: el del backoffice más el enlace <c>wa.me</c> ya armado, para que el storefront no tenga que
/// saber limpiar un número. Va siempre, null sin número: el storefront lo lee como obligatorio y anulable.
/// </summary>
/// <param name="WhatsAppUrl"><c>https://wa.me/</c> más sólo los dígitos del número (el formato que exige wa.me, sin «+» ni espacios).</param>
public sealed record PublicStoreContactDto(
    string? WhatsApp,
    string? WhatsAppUrl,
    string? Address,
    string? Schedule,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TiktokUrl)
{
    public static PublicStoreContactDto From(StoreContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return new PublicStoreContactDto(
            contact.WhatsApp,
            WhatsAppUrlOf(contact.WhatsApp),
            contact.Address,
            contact.Schedule,
            contact.InstagramUrl,
            contact.FacebookUrl,
            contact.TiktokUrl);
    }

    private static string? WhatsAppUrlOf(string? whatsApp)
    {
        var digits = new string((whatsApp ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : $"https://wa.me/{digits}";
    }
}

/// <param name="Colors">Sólo los colores ya resueltos; los contrastes y los overrides son del backoffice.</param>
/// <param name="Currencies">Los códigos en el orden guardado; la principal se informa aparte y la tienda decide cuál muestra primero.</param>
/// <param name="FeaturedCategories">Las destacadas que tienen productos en alguna de las monedas de la tienda; la app las cruza con la moneda elegida.</param>
public sealed record PublicStoreDto(
    string Name,
    string? LogoUrl,
    StoreColorsDto? Colors,
    PublicBannerDto Banner,
    PublicStoreContactDto Contact,
    string? DataPolicyUrl,
    string PricingMode,
    IReadOnlyList<string> Currencies,
    string? PrimaryCurrency,
    IReadOnlyList<string> SalesCountries,
    IReadOnlyList<StoreFeaturedCategoryDto> FeaturedCategories,
    PublicRegionalFormatDto RegionalFormat);

/// <param name="ProductCount">Acumulado: los productos de la categoría y los de todas sus descendientes, en la moneda pedida.</param>
public sealed record PublicCategoryDto(Guid Id, string Name, Guid? ParentId, int ProductCount);

public sealed record PublicCategoriesDto(string Currency, IReadOnlyList<PublicCategoryDto> Items);

/// <param name="PriceFrom">El precio de lista más bajo entre las variantes vendibles en la moneda, IVA incluido.</param>
public sealed record PublicProductCardDto(
    Guid Id, string Name, Guid? CategoryId, string? ImageUrl, decimal PriceFrom, decimal PriceTo, bool HasVariants);

public sealed record PublicProductsPageDto(
    string Currency, IReadOnlyList<PublicProductCardDto> Items, int Total, int Page, int PageSize);

/// <param name="Final">El precio por unidad con este descuento, derivado en el servidor.</param>
public sealed record PublicScaleDto(
    int FromUnit, int ToUnit, decimal Discount, decimal Final, string Restriction, int? Multiple, bool AllowGrouping);

/// <param name="Scales">Vacías en una tienda «sólo detal».</param>
/// <param name="OptionValueIds">Los ids de <see cref="PublicOptionValueDto"/> que arman la variante; vacía en un producto sin opciones.</param>
public sealed record PublicVariantDto(
    Guid Id,
    string Name,
    IReadOnlyList<Guid> OptionValueIds,
    string? ImageUrl,
    decimal Price,
    IReadOnlyList<int> PackagingUnits,
    IReadOnlyList<PublicScaleDto> Scales);

/// <param name="Images">La galería en orden; la primera es la principal.</param>
/// <param name="Options">Las opciones con los valores que alguna variante vendible usa: el selector de la ficha.</param>
public sealed record PublicProductDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? CategoryId,
    string? ImageUrl,
    IReadOnlyList<string> Images,
    IReadOnlyList<PublicOptionDto> Options,
    string Currency,
    IReadOnlyList<PublicVariantDto> Variants);

public sealed record PublicOptionDto(string Name, IReadOnlyList<PublicOptionValueDto> Values);

public sealed record PublicOptionValueDto(Guid Id, string Name);

public sealed record PublicGeoItemDto(Guid Id, string Name);

public sealed record PublicGeoListDto(IReadOnlyList<PublicGeoItemDto> Items);

/// <summary>El cuerpo de <c>POST /cart/quote</c>. Todo anulable en el binding: un campo ausente es un 422 y no un 400 del deserializador.</summary>
public sealed record PublicCartRequest(string? Currency, IReadOnlyList<PublicCartLineRequest?>? Lines);

public sealed record PublicCartLineRequest(Guid VariantId, int Quantity);

public sealed record PublicCartQuoteLineDto(
    Guid VariantId,
    Guid ProductId,
    string Name,
    string? ImageUrl,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    bool ScaleApplied,
    decimal DiscountedUnitPrice,
    decimal LineTotal);

/// <param name="Unavailable">Las variantes del carrito que la tienda ya no vende en esa moneda. Se reportan y no se cobran.</param>
public sealed record PublicCartQuoteDto(
    string Currency,
    IReadOnlyList<PublicCartQuoteLineDto> Lines,
    IReadOnlyList<Guid> Unavailable,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Total);
