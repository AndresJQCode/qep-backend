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
}

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
