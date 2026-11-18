using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Lo que el PUT de la consola no puede cambiar en un producto que administra otra plataforma
/// (spec 2026-11-17 §5.5, plan D7): nombre, opciones, alta y baja de variantes (incluido
/// desactivar una), código, código de barras y precios de lista. Lo pisaría el siguiente webhook de
/// Shopify, así que se rechaza al guardar en vez de perderse en silencio después. La tasa, las
/// escalas, los empaques, los costos, la descripción, la categoría, la galería y la visibilidad en
/// la tienda se siguen editando en QEP.
///
/// Va en el handler y no en el validador de FluentValidation porque necesita el producto guardado.
/// Sale como <see cref="CatalogDomainException"/> con el mapa de campos: 422 con <c>code</c> y
/// <c>errors</c>, el único 422 que el formulario sabe leer para marcar el input.
/// </summary>
internal static class ExternallyManagedProducts
{
    public const string Code = "catalog.product.managed_externally";

    private const string Message = "Este dato lo administra Shopify.";

    public static void EnsureAllowed(Product product, UpdateProductCommand command)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(command);

        if (product.ExternalSource is null)
        {
            return;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Recortado, como lo guarda el agregado: «Camiseta » no es un cambio.
        if (!string.Equals(command.Name?.Trim(), product.Name, StringComparison.Ordinal))
        {
            errors[nameof(UpdateProductCommand.Name)] = [Message];
        }

        if (!(command.OptionIds ?? []).SequenceEqual(product.OptionIds))
        {
            errors[nameof(UpdateProductCommand.OptionIds)] = [Message];
        }

        if (command.DeletedVariantIds is { Count: > 0 })
        {
            errors[nameof(UpdateProductCommand.DeletedVariantIds)] = [Message];
        }

        var saved = product.Variants.ToDictionary(variant => variant.Id.Value);
        var body = command.Variants ?? [];
        var requested = body.Where(row => row.Id is not null).Select(row => row.Id!.Value).ToHashSet();

        // Una fila sin id es una variante nueva; una guardada que no viene es una baja.
        if (body.Any(row => row.Id is null) || saved.Keys.Any(id => !requested.Contains(id)))
        {
            errors[nameof(UpdateProductCommand.Variants)] = [Message];
        }

        for (var index = 0; index < body.Count; index++)
        {
            var row = body[index];
            if (row.Id is not { } id || !saved.TryGetValue(id, out var variant))
            {
                continue;
            }

            var prefix = $"{nameof(UpdateProductCommand.Variants)}[{index}]";
            if (!string.Equals(Trimmed(row.Code), variant.Code, StringComparison.Ordinal))
            {
                errors[$"{prefix}.{nameof(ProductVariantRequest.Code)}"] = [Message];
            }

            if (!string.Equals(Trimmed(row.Barcode), variant.Barcode, StringComparison.Ordinal))
            {
                errors[$"{prefix}.{nameof(ProductVariantRequest.Barcode)}"] = [Message];
            }

            // null hereda el estado guardado (ProductVariantChanges): no es un cambio.
            if (row.IsActive is { } isActive && isActive != variant.IsActive)
            {
                errors[$"{prefix}.{nameof(ProductVariantRequest.IsActive)}"] = [Message];
            }

            // Pricing null hereda los precios guardados; las escalas y los empaques sí se editan.
            if (row.Pricing is { } pricing && !SamePrices(pricing.Prices, variant))
            {
                errors[$"{prefix}.{nameof(ProductVariantRequest.Pricing)}.{nameof(ProductPricingRequest.Prices)}"] = [Message];
            }
        }

        if (errors.Count > 0)
        {
            throw new CatalogDomainException(
                Code,
                "The product is managed by an external platform; these fields cannot be changed in QEP.",
                errors);
        }
    }

    // La misma normalización que el agregado aplica a código y barras (ProductVariant.NormalizeCode y
    // NormalizeBarcode, internas del dominio): recortar, y vacío es ausente.
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SamePrices(IReadOnlyDictionary<string, decimal>? requested, ProductVariant variant)
    {
        // El validador ya corrió, así que las monedas son del catálogo; Normalize sólo las lleva a la
        // forma guardada.
        var prices = (requested ?? new Dictionary<string, decimal>())
            .ToDictionary(entry => Currencies.Normalize(entry.Key), entry => entry.Value, StringComparer.Ordinal);
        return prices.Count == variant.Prices.Count
            && variant.Prices.All(price => prices.TryGetValue(price.Currency, out var amount) && amount == price.Amount);
    }
}
