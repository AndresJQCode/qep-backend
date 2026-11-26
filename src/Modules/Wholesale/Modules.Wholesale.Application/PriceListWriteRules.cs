using FluentValidation;
using FluentValidation.Results;

namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-22: las reglas de los términos que recibe la vista previa del distribuidor. Son las mismas de la lista en Catalog
/// (<c>PriceListWriteRules</c> de Catalog), copiadas porque Mayorista no referencia Catalog: un cambio de topes o rangos se hace en
/// los dos lados. Sin nombre: la vista previa no guarda nada.
/// </summary>
public sealed class PreviewPriceListValidator : AbstractValidator<PreviewPriceListCommand>
{
    // Los de Catalog: PriceList.MinAdjustment, MaxAdjustment, MaxCategories, MaxHiddenCategories y MaxHiddenProducts.
    internal const decimal MinAdjustment = -100m;
    internal const decimal MaxAdjustment = 999.99m;
    internal const int MaxCategories = 200;
    internal const int MaxHiddenCategories = 200;
    internal const int MaxHiddenProducts = 1000;

    public PreviewPriceListValidator()
    {
        RuleFor(command => command.AdjustmentPercent)
            .InclusiveBetween(MinAdjustment, MaxAdjustment)
            .PrecisionScale(5, 2, ignoreTrailingZeros: true);
        RuleFor(command => command.Visibility)
            .Must(visibility => PreviewTerms.TryParseVisibility(visibility, out _))
            .WithMessage("Visibility must be AllActive or Categories.");

        RuleFor(command => command.Exclusions).Custom((exclusions, context) =>
        {
            if (exclusions is null)
            {
                return;
            }

            var seen = new HashSet<(bool, Guid)>();
            var categories = 0;
            var products = 0;
            for (var index = 0; index < exclusions.Count; index++)
            {
                var item = exclusions[index];
                if (item is null || !PreviewTerms.TryParseKind(item.Kind, out var isCategory) || item.Id == Guid.Empty
                    || !seen.Add((isCategory, item.Id)))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Exclusions[{index}]", "The kind is not Category or Product, the id is empty, or it is already in the list."));
                    continue;
                }

                if (isCategory)
                {
                    categories++;
                }
                else
                {
                    products++;
                }
            }

            if (categories > MaxHiddenCategories || products > MaxHiddenProducts)
            {
                context.AddFailure(new ValidationFailure(
                    "Exclusions", $"A price list hides at most {MaxHiddenCategories} categories and {MaxHiddenProducts} products."));
            }
        });

        When(command => PreviewTerms.IsCategories(command.Visibility), () =>
        {
            RuleFor(command => command.CategoryIds)
                .Must(ids => ids is { Count: > 0 })
                .WithMessage("Choose at least one category.")
                .Must(ids => ids is null || ids.Count <= MaxCategories)
                .WithMessage($"A price list can have at most {MaxCategories} categories.");
            RuleFor(command => command.CategoryIds).Custom((ids, context) =>
            {
                if (ids is null)
                {
                    return;
                }

                var seen = new HashSet<Guid>();
                for (var index = 0; index < ids.Count; index++)
                {
                    if (ids[index] == Guid.Empty || !seen.Add(ids[index]))
                    {
                        context.AddFailure(new ValidationFailure($"CategoryIds[{index}]", "The category is empty or already in the list."));
                    }
                }
            });
        });
    }
}

/// <summary>Una exclusión como llega en el cuerpo: <c>Kind</c> es <c>Category</c> o <c>Product</c>.</summary>
public sealed record PriceListExclusionInput(string Kind, Guid Id);

internal static class PreviewTerms
{
    public static bool TryParseVisibility(string? value, out bool isCategories)
    {
        isCategories = false;
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, "AllActive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        isCategories = string.Equals(trimmed, "Categories", StringComparison.OrdinalIgnoreCase);
        return isCategories;
    }

    public static bool IsCategories(string? visibility) => TryParseVisibility(visibility, out var isCategories) && isCategories;

    public static bool TryParseKind(string? value, out bool isCategory)
    {
        var trimmed = value?.Trim();
        isCategory = string.Equals(trimmed, "Category", StringComparison.OrdinalIgnoreCase);
        return isCategory || string.Equals(trimmed, "Product", StringComparison.OrdinalIgnoreCase);
    }
}
