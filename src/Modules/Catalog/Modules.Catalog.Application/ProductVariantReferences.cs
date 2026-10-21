using FluentValidation.Results;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Lo que el agregado no puede revisar porque no ve la base: que las opciones y los valores
/// existan en la biblioteca **del tenant**, que cada valor sea de la opción de su posición, y que
/// los códigos no los use ya otro producto. Corre en el handler **antes** de guardar.
///
/// Sin esto, una opción o un valor ajeno o inexistente llega a las FK de
/// <c>product_options</c>/<c>product_variant_option_values</c> y sale como 500 (la unidad de
/// trabajo sólo traduce esas FK cuando se está borrando una opción). Y un valor de otra opción
/// entraría sin error: la FK sólo dice que el valor existe, no de qué opción es ni de qué tenant.
///
/// Todo sale como <see cref="CatalogDomainException"/> con el código de la primera falla —el mismo
/// que daría el dominio o el índice, que el frontend ya conoce (<c>catalog.product.code_taken</c>)—
/// **y** el mapa <c>errors</c> con todas las fallas por campo, para marcar cada input.
/// </summary>
internal static class ProductVariantReferences
{
    /// <param name="product">El producto guardado, o null al crearlo. Una opción o un valor
    /// inactivo se acepta sólo si el producto ya lo usaba (spec §3.1: «las existentes lo
    /// conservan»); para uno nuevo, no se ofrece.</param>
    public static void EnsureValid(
        Product? product,
        IReadOnlyList<Guid> optionIds,
        IReadOnlyList<ProductVariantInput> variants,
        IReadOnlyDictionary<Guid, OptionDefinition> library)
    {
        var failures = new List<ValidationFailure>();
        var usedOptions = product?.OptionIds.ToHashSet() ?? [];
        var usedValues = product?.Variants.SelectMany(variant => variant.OptionValueIds).ToHashSet() ?? [];

        var options = new OptionDefinition?[optionIds.Count];
        for (var index = 0; index < optionIds.Count; index++)
        {
            var field = $"{nameof(IProductWriteCommand.OptionIds)}[{index}]";
            if (!library.TryGetValue(optionIds[index], out var option))
            {
                // Mismo código para «no existe» y «es de otro tenant», como las imágenes: distinguirlos
                // confirmaría que el id existe en otro lado.
                failures.Add(ProductWriteRules.Failure(
                    field, "catalog.option.not_found", "Esa opción no existe en tu catálogo."));
                continue;
            }

            if (!option.IsActive && !usedOptions.Contains(option.Id.Value))
            {
                failures.Add(ProductWriteRules.Failure(
                    field, "catalog.option.inactive", "Esa opción está inactiva: actívala en la biblioteca para usarla."));
            }

            options[index] = option;
        }

        var activeCombinations = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < variants.Count; index++)
        {
            var variant = variants[index];
            var field = $"{nameof(IProductWriteCommand.Variants)}[{index}].{nameof(ProductVariantRequest.OptionValueIds)}";

            if (variant.OptionValueIds.Count != optionIds.Count)
            {
                failures.Add(ProductWriteRules.Failure(
                    field, "catalog.variant.value_count_mismatch", "Elige un valor para cada opción del producto."));
                continue;
            }

            for (var position = 0; position < optionIds.Count; position++)
            {
                if (options[position] is not { } option)
                {
                    continue;
                }

                var valueId = variant.OptionValueIds[position];
                var value = option.Values.FirstOrDefault(candidate => candidate.Id == valueId);
                if (value is null)
                {
                    failures.Add(ProductWriteRules.Failure(
                        $"{field}[{position}]",
                        "catalog.option.value_not_found",
                        $"Ese valor no es de la opción «{option.Name}»."));
                }
                else if (!value.IsActive && !usedValues.Contains(value.Id))
                {
                    failures.Add(ProductWriteRules.Failure(
                        $"{field}[{position}]",
                        "catalog.option.value_inactive",
                        $"El valor «{value.Value}» está inactivo: actívalo en la biblioteca para usarlo."));
                }
            }

            // Sólo entre activas (spec §3.3.1), como el agregado; acá con el campo marcado.
            if (variant.IsActive && !activeCombinations.Add(string.Join(',', variant.OptionValueIds)))
            {
                failures.Add(ProductWriteRules.Failure(
                    field,
                    "catalog.variant.combination_duplicate",
                    "Ya hay otra variante activa con esta combinación."));
            }
        }

        // Decisión de las variantes (2026-10-20): el dominio no lo exige, pero un producto activo sin ninguna
        // variante activa no se puede vender y no se distingue de uno inactivo. Para eso está
        // /deactivate.
        if (!variants.Any(variant => variant.IsActive))
        {
            failures.Add(ProductWriteRules.Failure(
                nameof(IProductWriteCommand.Variants),
                "catalog.variant.active_required",
                "Deja al menos una variante activa; para dejar de vender el producto, desactívalo."));
        }

        ThrowIfAny(failures);
    }

    /// <summary>
    /// Códigos y códigos de barras que ya usa **otro** producto del tenant, con el campo marcado.
    /// No reemplaza a los índices únicos —dos guardados a la vez pasan los dos por acá, y el que
    /// llega segundo lo frena el índice, traducido por nombre en <c>CatalogUnitOfWork</c>—: le da
    /// al caso normal el campo que el índice no sabe dar.
    ///
    /// Los del propio producto se excluyen: una variante que conserva su código no choca consigo
    /// misma. Eso deja pasar el intercambio de códigos entre dos variantes del mismo producto, que
    /// luego choca con el índice (ver <c>UpdateProductHandler</c>).
    /// </summary>
    public static async Task EnsureCodesAvailableAsync(
        IProductRepository repository,
        Guid tenantId,
        Product? product,
        IReadOnlyList<ProductVariantInput> variants,
        CancellationToken cancellationToken)
    {
        var codes = variants.Select(variant => variant.Code.Trim()).ToArray();
        var barcodes = variants.Select(variant => variant.Barcode?.Trim() is { Length: > 0 } barcode ? barcode : null).ToArray();

        var ownCodes = product?.Variants.Select(variant => variant.Code).ToHashSet(StringComparer.Ordinal) ?? [];
        var ownBarcodes = product?.Variants.Select(variant => variant.Barcode).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];

        var takenCodes = await repository.FindExistingCodesAsync(
            tenantId, codes.Where(code => code.Length > 0).Distinct().ToArray(), cancellationToken);
        var takenBarcodes = await repository.FindExistingBarcodesAsync(
            tenantId, barcodes.OfType<string>().Distinct().ToArray(), cancellationToken);

        var failures = new List<ValidationFailure>();
        for (var index = 0; index < variants.Count; index++)
        {
            var field = $"{nameof(IProductWriteCommand.Variants)}[{index}]";
            if (takenCodes.Contains(codes[index]) && !ownCodes.Contains(codes[index]))
            {
                failures.Add(ProductWriteRules.Failure(
                    $"{field}.{nameof(ProductVariantRequest.Code)}",
                    "catalog.product.code_taken",
                    "Otro producto ya usa este código."));
            }

            if (barcodes[index] is { } barcode && takenBarcodes.Contains(barcode) && !ownBarcodes.Contains(barcode))
            {
                failures.Add(ProductWriteRules.Failure(
                    $"{field}.{nameof(ProductVariantRequest.Barcode)}",
                    "catalog.variant.barcode_taken",
                    "Otro producto ya usa este código de barras."));
            }
        }

        ThrowIfAny(failures);
    }

    private static void ThrowIfAny(List<ValidationFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        throw new CatalogDomainException(
            failures[0].ErrorCode,
            failures[0].ErrorMessage,
            failures
                .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).ToArray(),
                    StringComparer.Ordinal));
    }
}
