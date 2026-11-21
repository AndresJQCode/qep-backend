using FluentValidation;
using FluentValidation.Results;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Lo que comparten el alta, la edición y la vista previa: los términos de la lista. Los comandos son planos para que
/// <c>errors</c> diga <c>CategoryIds[1]</c> y no <c>Body.CategoryIds…</c>. <c>Visibility</c> llega como texto para que un valor
/// raro sea un 422 con <c>errors.Visibility</c> y no un 400 del binding.
/// </summary>
public interface IPriceListTermsCommand
{
    Guid TenantId { get; }

    decimal DiscountPercent { get; }

    string Visibility { get; }

    IReadOnlyList<Guid> CategoryIds { get; }

    /// <summary>Spec 2026-11-20: lo que la lista oculta. <c>null</c> o vacía = nada.</summary>
    IReadOnlyList<PriceListExclusionInput>? Exclusions { get; }
}

/// <summary>
/// Una exclusión como llega en el cuerpo. <c>Kind</c> va como texto para que un valor raro sea un 422 con
/// <c>errors.Exclusions[i]</c> y no un 400 del binding (mismo criterio que <c>Visibility</c>).
/// </summary>
public sealed record PriceListExclusionInput(string Kind, Guid Id);

/// <summary>Las reglas de los términos (contrato «Listas de precios»). El nombre lo suman los validadores de alta y edición.</summary>
internal sealed class PriceListWriteRules : AbstractValidator<IPriceListTermsCommand>
{
    // La columna es numeric(5,2): 0-100 con a lo sumo dos decimales. Los ceros a la derecha no cuentan: 12.50 vale.
    private const int DiscountPrecision = 5;

    private const int DiscountScale = 2;

    public PriceListWriteRules()
    {
        RuleFor(command => command.DiscountPercent)
            .InclusiveBetween(0m, 100m)
            .PrecisionScale(DiscountPrecision, DiscountScale, ignoreTrailingZeros: true);
        RuleFor(command => command.Visibility)
            .Must(visibility => PriceListTerms.TryParseVisibility(visibility, out _))
            .WithMessage("Visibility must be AllActive or Categories.");

        RuleFor(command => command.Exclusions).Custom((exclusions, context) =>
        {
            if (exclusions is null)
            {
                return;
            }

            var seen = new HashSet<(PriceListExclusionKind, Guid)>();
            var categories = 0;
            var products = 0;
            for (var index = 0; index < exclusions.Count; index++)
            {
                var item = exclusions[index];
                if (item is null || !PriceListTerms.TryParseKind(item.Kind, out var kind) || item.Id == Guid.Empty
                    || !seen.Add((kind, item.Id)))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Exclusions[{index}]", "The kind is not Category or Product, the id is empty, or it is already in the list."));
                    continue;
                }

                if (kind == PriceListExclusionKind.Category)
                {
                    categories++;
                }
                else
                {
                    products++;
                }
            }

            if (categories > PriceList.MaxHiddenCategories || products > PriceList.MaxHiddenProducts)
            {
                context.AddFailure(new ValidationFailure(
                    "Exclusions",
                    $"A price list hides at most {PriceList.MaxHiddenCategories} categories and {PriceList.MaxHiddenProducts} products."));
            }
        });

        // Con AllActive las categorías se ignoran y se guarda la lista vacía: no hay nada que validar.
        When(command => IsCategories(command.Visibility), () =>
        {
            RuleFor(command => command.CategoryIds)
                .Must(ids => ids is { Count: > 0 })
                .WithMessage("Choose at least one category.")
                .Must(ids => ids is null || ids.Count <= PriceList.MaxCategories)
                .WithMessage($"A price list can have at most {PriceList.MaxCategories} categories.");
            RuleFor(command => command.CategoryIds).Custom((ids, context) =>
            {
                if (ids is null)
                {
                    return;
                }

                // La segunda aparición es la que sobra, igual que en la pantalla.
                var seen = new HashSet<Guid>();
                for (var index = 0; index < ids.Count; index++)
                {
                    if (ids[index] == Guid.Empty || !seen.Add(ids[index]))
                    {
                        context.AddFailure(new ValidationFailure(
                            $"CategoryIds[{index}]", "The category is empty or already in the list."));
                    }
                }
            });
        });
    }

    private static bool IsCategories(string? visibility) =>
        PriceListTerms.TryParseVisibility(visibility, out var parsed) && parsed == PriceListVisibility.Categories;
}

public sealed class CreatePriceListValidator : AbstractValidator<CreatePriceListCommand>
{
    public CreatePriceListValidator()
    {
        Include(new PriceListWriteRules());
        RuleFor(command => command.Name).PriceListName();
    }
}

public sealed class UpdatePriceListValidator : AbstractValidator<UpdatePriceListCommand>
{
    public UpdatePriceListValidator()
    {
        Include(new PriceListWriteRules());
        RuleFor(command => command.Name).PriceListName();
    }
}

public sealed class PreviewPriceListValidator : AbstractValidator<PreviewPriceListCommand>
{
    public PreviewPriceListValidator() => Include(new PriceListWriteRules());
}

/// <summary>El nombre recortado, 1-120 (el mismo tope del agregado).</summary>
internal static class PriceListNameRules
{
    public static IRuleBuilderOptions<T, string> PriceListName<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("The price list needs a name.")
            .Must(name => name is null || name.Trim().Length <= PriceList.NameMaxLength)
            .WithMessage($"The price list name has at most {PriceList.NameMaxLength} characters.");
}

/// <summary>Lo que los handlers hacen igual con los términos ya validados.</summary>
internal static class PriceListTerms
{
    /// <summary>
    /// Por nombre, sin importar mayúsculas. Se compara con los nombres y no con <c>Enum.TryParse</c> a secas, que aceptaría
    /// <c>"1"</c>.
    /// </summary>
    public static bool TryParseVisibility(string? value, out PriceListVisibility visibility)
    {
        visibility = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        foreach (var candidate in Enum.GetValues<PriceListVisibility>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                visibility = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Por nombre, sin importar mayúsculas; no acepta números (como <see cref="TryParseVisibility"/>).</summary>
    public static bool TryParseKind(string? value, out PriceListExclusionKind kind)
    {
        kind = default;
        var trimmed = value?.Trim();
        foreach (var candidate in Enum.GetValues<PriceListExclusionKind>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Las exclusiones del cuerpo ya validadas, como las guarda el dominio.</summary>
    public static IReadOnlyList<PriceListExclusion> ExclusionsOf(IPriceListTermsCommand command) =>
        (command.Exclusions ?? [])
            .Select(item => TryParseKind(item.Kind, out var kind)
                ? new PriceListExclusion(kind, item.Id)
                : throw new InvalidOperationException($"Exclusion kind '{item.Kind}' passed validation but is not known."))
            .ToList();

    /// <summary>
    /// Spec 2026-11-20: al guardar, cada categoría o producto excluido tiene que ser del tenant (como D-9 para
    /// <c>CategoryIds</c>). Una ajena o borrada es 422 con <c>errors.Exclusions[i]</c>, el índice del cuerpo.
    /// </summary>
    public static async Task RequireExclusionsAsync(
        IWholesaleCatalogReader catalog, Guid tenantId, IPriceListTermsCommand command, CancellationToken cancellationToken)
    {
        var parsed = ExclusionsOf(command);
        if (parsed.Count == 0)
        {
            return;
        }

        var categoryIds = parsed.Where(item => item.Kind == PriceListExclusionKind.Category).Select(item => item.Id).Distinct().ToArray();
        var productIds = parsed.Where(item => item.Kind == PriceListExclusionKind.Product).Select(item => item.Id).Distinct().ToArray();
        var categories = categoryIds.Length == 0
            ? new Dictionary<Guid, WholesaleCategoryRef>()
            : await catalog.FindCategoriesAsync(tenantId, categoryIds, cancellationToken);
        var products = productIds.Length == 0
            ? new Dictionary<Guid, WholesaleProductRef>()
            : await catalog.FindProductsAsync(tenantId, productIds, cancellationToken);

        var failures = new List<ValidationFailure>();
        for (var index = 0; index < parsed.Count; index++)
        {
            var found = parsed[index].Kind == PriceListExclusionKind.Category
                ? categories.ContainsKey(parsed[index].Id)
                : products.ContainsKey(parsed[index].Id);
            if (!found)
            {
                failures.Add(new ValidationFailure($"Exclusions[{index}]", "The category or product does not exist in the catalog."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    /// <summary>
    /// El detalle de las exclusiones guardadas: categorías primero y luego productos, cada grupo por nombre. Un destino que ya no
    /// existe no viaja (como D-23); el siguiente <c>PUT</c> guarda el conjunto limpio.
    /// </summary>
    public static async Task<IReadOnlyList<PriceListExclusionDto>> ExclusionDtosAsync(
        IWholesaleCatalogReader catalog, Guid tenantId, PriceList list, CancellationToken cancellationToken)
    {
        if (list.Exclusions.Count == 0)
        {
            return [];
        }

        var products = list.ExcludedProductIds.Count == 0
            ? new Dictionary<Guid, WholesaleProductRef>()
            : await catalog.FindProductsAsync(tenantId, list.ExcludedProductIds, cancellationToken);
        var categoryIds = list.ExcludedCategoryIds
            .Concat(products.Values.Where(product => product.CategoryId is not null).Select(product => product.CategoryId!.Value))
            .Distinct()
            .ToArray();
        var categories = categoryIds.Length == 0
            ? new Dictionary<Guid, WholesaleCategoryRef>()
            : await catalog.FindCategoriesAsync(tenantId, categoryIds, cancellationToken);

        var hiddenCategories = list.ExcludedCategoryIds
            .Where(categories.ContainsKey)
            .Select(id => new PriceListExclusionDto(nameof(PriceListExclusionKind.Category), id, categories[id].Name, null, null));
        var hiddenProducts = list.ExcludedProductIds
            .Where(products.ContainsKey)
            .Select(id => products[id])
            .Select(product => new PriceListExclusionDto(
                nameof(PriceListExclusionKind.Product),
                product.Id,
                product.Name,
                product.Code,
                product.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var category) ? category.Name : null));

        return
        [
            .. hiddenCategories.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id),
            .. hiddenProducts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id),
        ];
    }

    public static PriceListVisibility VisibilityOf(IPriceListTermsCommand command) =>
        TryParseVisibility(command.Visibility, out var visibility)
            ? visibility
            : throw new InvalidOperationException($"Visibility '{command.Visibility}' passed validation but is not known.");

    /// <summary>Los ids que guarda la lista: vacía con <c>AllActive</c>, los del cuerpo con <c>Categories</c>.</summary>
    public static IReadOnlyList<Guid> CategoryIdsOf(IPriceListTermsCommand command) =>
        VisibilityOf(command) == PriceListVisibility.AllActive ? [] : command.CategoryIds;

    /// <summary>
    /// Las categorías del cuerpo con su nombre. Al guardar, todas tienen que ser del tenant (D-9): una ajena o borrada es 422
    /// <c>category_not_found</c>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> RequireCategoriesAsync(
        IWholesaleCatalogReader catalog, Guid tenantId, IReadOnlyList<Guid> categoryIds, CancellationToken cancellationToken)
    {
        if (categoryIds.Count == 0)
        {
            return new Dictionary<Guid, WholesaleCategoryRef>();
        }

        var found = await catalog.FindCategoriesAsync(tenantId, categoryIds, cancellationToken);
        if (categoryIds.Any(id => !found.ContainsKey(id)))
        {
            throw WholesaleConflicts.CategoryNotFound();
        }

        return found;
    }
}
