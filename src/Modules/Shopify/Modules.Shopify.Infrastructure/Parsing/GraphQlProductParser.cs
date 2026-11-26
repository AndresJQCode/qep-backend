using System.Text.Json;
using Modules.Shopify.Application;
using static Modules.Shopify.Infrastructure.Parsing.ShopifyJson;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>
/// Spec 2026-11-17 §5.3: un nodo <c>Product</c> de la Admin API GraphQL (los campos de <c>ShopifyProductsClient.ProductFields</c>)
/// a <see cref="ShopifyProductSnapshot"/>. Las variantes tienen que venir ya unidas (todas las páginas). Los textos van tal cual
/// (el mapeo recorta); un SKU o código de barras vacío es <c>null</c>. <c>selectedOptions</c> se ordenan como <c>options</c>, por
/// nombre exacto.
/// </summary>
internal static class GraphQlProductParser
{
    public static ShopifyProductSnapshot Parse(JsonElement product)
    {
        if (product.ValueKind != JsonValueKind.Object)
        {
            throw new ShopifyPayloadException(string.Empty, $"The Shopify product must be a JSON object, not {product.ValueKind}.");
        }

        var id = OptionalId(product, "id", "id", "Product") ?? throw Missing("id");
        var options = Items(product, "options", "options")
            .Select((option, index) => new ShopifyProductOption(
                RequiredString(option, "name", $"options[{index}].name"),
                Items(option, "optionValues", $"options[{index}].optionValues")
                    .Select((value, valueIndex) => RequiredString(value, "name", $"options[{index}].optionValues[{valueIndex}].name"))
                    .ToArray()))
            .ToArray();

        var variants = Items(product, "variants", "variants", connection: true)
            .Select((variant, index) => Variant(variant, index, options))
            .ToArray();

        return new ShopifyProductSnapshot(
            id,
            RequiredString(product, "title", "title"),
            Status(OptionalString(product, "status", "status")),
            RequiredDate(product, "updatedAt", "updatedAt"),
            options,
            variants,
            FeaturedImage(product));
    }

    private static ShopifyVariantSnapshot Variant(JsonElement variant, int index, IReadOnlyList<ShopifyProductOption> options)
    {
        var field = $"variants[{index}]";
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (option, optionIndex) in Items(variant, "selectedOptions", $"{field}.selectedOptions").Select((option, i) => (option, i)))
        {
            selected[RequiredString(option, "name", $"{field}.selectedOptions[{optionIndex}].name")] =
                RequiredString(option, "value", $"{field}.selectedOptions[{optionIndex}].value");
        }

        // En el orden de options; una opción sin valor elegido es una respuesta rota.
        var values = options
            .Select(option => selected.TryGetValue(option.Name, out var value)
                ? value
                : throw new ShopifyPayloadException($"{field}.selectedOptions", $"The variant has no value for the option '{option.Name}'."))
            .ToArray();

        return new ShopifyVariantSnapshot(
            OptionalId(variant, "id", $"{field}.id", "ProductVariant") ?? throw Missing($"{field}.id"),
            OptionalString(variant, "sku", $"{field}.sku"),
            OptionalString(variant, "barcode", $"{field}.barcode"),
            Find(variant, "price") is { } price ? Decimal(price, $"{field}.price") : throw Missing($"{field}.price"),
            Boolean(variant, "taxable", $"{field}.taxable"),
            values);
    }

    // Plan D1: el id del MediaImage, en el mismo espacio para el webhook y la importación. Un video u otro tipo de media no es imagen.
    private static ShopifyProductImage? FeaturedImage(JsonElement product)
    {
        if (Find(product, "featuredMedia") is not { } media
            || OptionalString(media, "id", "featuredMedia.id") is not { } gid
            || !gid.StartsWith("gid://shopify/MediaImage/", StringComparison.Ordinal)
            || FindPath(media, "preview.image.url") is not { ValueKind: JsonValueKind.String } url
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new ShopifyProductImage(OptionalId(media, "id", "featuredMedia.id", "MediaImage")!.Value, uri);
    }

    private static ShopifyProductStatus Status(string? status) => status switch
    {
        "ACTIVE" => ShopifyProductStatus.Active,
        "DRAFT" => ShopifyProductStatus.Draft,
        "ARCHIVED" => ShopifyProductStatus.Archived,
        null => throw Missing("status"),
        _ => throw new ShopifyPayloadException("status", $"The Shopify product status '{status}' is not known."),
    };
}
