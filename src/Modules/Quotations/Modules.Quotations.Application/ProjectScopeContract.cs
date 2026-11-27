using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Un ítem del alcance tal como llega. El <paramref name="Id"/> lo genera la pantalla.</summary>
public sealed record ProjectItemRequest(Guid Id, string? Text);

/// <summary>Una fase del alcance tal como llega; <paramref name="Items"/> ausente es «sin ítems».</summary>
public sealed record ProjectPhaseRequest(
    Guid Id, string? Name, decimal? Price, IReadOnlyList<ProjectItemRequest>? Items);

/// <summary>
/// El bloque <c>project</c> de un alta, un guardado o su vista previa (spec 2026-11-26 §6.3): reemplaza el alcance
/// entero. <paramref name="PricesCurrency"/> es la moneda en que la pantalla escribió los precios; no se guarda, sirve
/// para saber si valen para la moneda en que queda el documento.
/// </summary>
public sealed record ProjectScopeRequest(
    Guid? ServiceVariantId,
    string? PricingMode,
    decimal? GlobalPrice,
    IReadOnlyList<ProjectPhaseRequest>? Phases,
    IReadOnlyList<ProjectItemRequest>? Items,
    string? PricesCurrency)
{
    /// <summary>El alcance del dominio. Corre después de <see cref="ProjectScopeRequestValidator"/>: lo que ese rechaza
    /// aquí lanza.</summary>
    public ProjectScope ToDomain() => ProjectScope.Create(
        ServiceVariantId,
        Enum.Parse<ProjectPricingMode>(PricingMode!, ignoreCase: true),
        GlobalPrice,
        [
            .. (Phases ?? []).Select(phase => new ProjectPhase(
                phase.Id, phase.Name!, phase.Price, ToDomainItems(phase.Items))),
        ],
        ToDomainItems(Items));

    private static ProjectItem[] ToDomainItems(IReadOnlyList<ProjectItemRequest>? items) =>
        [.. (items ?? []).Select(item => new ProjectItem(item.Id, item.Text!))];
}

public sealed record ProjectItemDto(Guid Id, string Text);

public sealed record ProjectPhaseDto(Guid Id, string Name, decimal? Price, IReadOnlyList<ProjectItemDto> Items);

/// <summary>
/// El alcance y el precio de un documento de proyecto como sale (spec 2026-11-26 §6.3). Viaja así en la respuesta HTTP.
/// BFF: <paramref name="Total"/> lo calcula el backend —la pantalla nunca suma precios de fase— y
/// <paramref name="PricesCurrency"/> es la moneda del documento, la que la pantalla manda de vuelta con los precios.
/// </summary>
public sealed record ProjectScopeDto(
    Guid? ServiceVariantId,
    string PricingMode,
    decimal? GlobalPrice,
    IReadOnlyList<ProjectPhaseDto> Phases,
    IReadOnlyList<ProjectItemDto> Items,
    string PricesCurrency,
    decimal? Total)
{
    /// <summary><c>null</c> en un documento <c>Standard</c>.</summary>
    public static ProjectScopeDto? From(ProjectScope? scope, string currency) =>
        scope is null
            ? null
            : new(
                scope.ServiceVariantId,
                scope.PricingMode.ToString(),
                scope.GlobalPrice,
                [
                    .. scope.Phases.Select(phase => new ProjectPhaseDto(
                        phase.Id, phase.Name, phase.Price, ToItemDtos(phase.Items))),
                ],
                ToItemDtos(scope.Items),
                currency,
                scope.Total);

    private static ProjectItemDto[] ToItemDtos(IReadOnlyList<ProjectItem> items) =>
        [.. items.Select(item => new ProjectItemDto(item.Id, item.Text))];
}

/// <summary>Los nombres de <see cref="QuotationKind"/> en el contrato, sin distinguir mayúsculas.</summary>
internal static class QuotationKindNames
{
    /// <summary>Ausente es <see cref="QuotationKind.Standard"/>; <c>null</c> si el texto no es ninguno de los
    /// dos. Por nombre y no con <c>Enum.TryParse</c>, que aceptaría «1».</summary>
    public static QuotationKind? Parse(string? kind) =>
        kind is null ? QuotationKind.Standard
        : string.Equals(kind, nameof(QuotationKind.Standard), StringComparison.OrdinalIgnoreCase) ? QuotationKind.Standard
        : string.Equals(kind, nameof(QuotationKind.Project), StringComparison.OrdinalIgnoreCase) ? QuotationKind.Project
        : null;
}

/// <summary>Las reglas de <c>kind</c> en el alta y su vista previa (spec 2026-11-26 §12-G): el guardado no lo recibe,
/// porque el tipo no cambia.</summary>
internal static class QuotationKindRules
{
    public static void AddTo<TEdits>(AbstractValidator<TEdits> validator, Func<TEdits, string?> kind)
        where TEdits : IQuotationEdits
    {
        validator.RuleFor(edits => kind(edits))
            .Must(value => QuotationKindNames.Parse(value) is not null)
            .WithMessage("El tipo de cotización debe ser Standard o Project.")
            .OverridePropertyName("Kind");
        validator.RuleFor(edits => edits.Project)
            .Null()
            .WithMessage("El bloque de proyecto sólo va en una cotización de tipo Project.")
            .When(edits => QuotationKindNames.Parse(kind(edits)) == QuotationKind.Standard)
            .OverridePropertyName("Project");
    }
}

/// <summary>
/// Las reglas de campo del bloque <c>project</c> (spec 2026-11-26 §6.3, P6 y §12-E). El dominio las repite como red
/// (<see cref="ProjectScope.Create"/>); estas existen para que el 422 traiga el campo en <c>errors</c>, con la posición
/// de la fase y del ítem. El tope de precio es el de las columnas <c>numeric(14,2)</c> de la línea y los totales.
/// </summary>
public sealed class ProjectScopeRequestValidator : AbstractValidator<ProjectScopeRequest>
{
    private static readonly string[] ModeNames = Enum.GetNames<ProjectPricingMode>();

    public ProjectScopeRequestValidator()
    {
        RuleFor(scope => scope.ServiceVariantId)
            .NotEqual((Guid?)Guid.Empty)
            .WithMessage("Elige un servicio del catálogo.");
        RuleFor(scope => scope.PricingMode)
            .Must(mode => ModeNames.Contains(mode, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"El modo de precio debe ser uno de estos: {string.Join(", ", ModeNames)}.");
        RuleFor(scope => scope.GlobalPrice).Custom(CheckPrice);
        RuleFor(scope => scope.PricesCurrency)
            .NotEmpty()
            .WithMessage("Indica en qué moneda escribiste los precios.")
            .When(HasPrices);
        RuleForEach(scope => scope.Phases).ChildRules(phase =>
        {
            phase.RuleFor(value => value.Id).NotEmpty();
            phase.RuleFor(value => value.Name)
                .NotEmpty()
                .WithMessage("Escribe el nombre de la fase.")
                .MaximumLength(ProjectScopeLimits.PhaseNameMaxLength);
            phase.RuleFor(value => value.Price).Custom(CheckPrice);
            phase.RuleForEach(value => value.Items).ChildRules(AddItemRules);
        });
        RuleForEach(scope => scope.Items).ChildRules(AddItemRules);
        RuleFor(scope => scope).Custom(CheckStructure);
    }

    private static void AddItemRules(InlineValidator<ProjectItemRequest> item)
    {
        item.RuleFor(value => value.Id).NotEmpty();
        item.RuleFor(value => value.Text)
            .NotEmpty()
            .WithMessage("Escribe el texto del ítem.")
            .MaximumLength(ProjectScopeLimits.ItemTextMaxLength);
    }

    private static void CheckPrice<T>(decimal? price, ValidationContext<T> context)
    {
        if (price < 0m)
        {
            context.AddFailure("El precio no puede ser negativo.");
        }
        else if (price > ProjectScopeLimits.MaxPrice)
        {
            context.AddFailure($"El precio no puede pasar de {ProjectScopeLimits.MaxPrice}.");
        }
    }

    private static bool HasPrices(ProjectScopeRequest scope) =>
        scope.GlobalPrice is not null || (scope.Phases ?? []).Any(phase => phase?.Price is not null);

    // Lo que cruza varios campos: los topes, los ids repetidos, lo que exige cada modo y la suma de las fases.
    private static void CheckStructure(ProjectScopeRequest scope, ValidationContext<ProjectScopeRequest> context)
    {
        var phases = scope.Phases ?? [];
        var looseItems = scope.Items ?? [];

        // Un `[null]` en el JSON: sin esto no tendría campo y reventaría al armar el alcance.
        if (phases.Any(phase => phase is null))
        {
            context.AddFailure(nameof(scope.Phases), "Hay una fase vacía en la lista.");
        }

        if (looseItems.Any(item => item is null)
            || phases.Any(phase => phase?.Items?.Any(item => item is null) == true))
        {
            context.AddFailure(nameof(scope.Items), "Hay un ítem vacío en la lista.");
        }

        if (phases.Count > ProjectScopeLimits.Phases)
        {
            context.AddFailure(
                nameof(scope.Phases), $"Un proyecto admite hasta {ProjectScopeLimits.Phases} fases.");
        }

        if (looseItems.Count + phases.Sum(phase => phase?.Items?.Count ?? 0) > ProjectScopeLimits.Items)
        {
            context.AddFailure(nameof(scope.Items), $"Un proyecto admite hasta {ProjectScopeLimits.Items} ítems.");
        }

        CheckIds(phases, looseItems, context);
        CheckMode(scope, phases, looseItems, context);
    }

    // El error va en el id repetido: primero las fases con sus ítems y después los sueltos, que es como se leen.
    private static void CheckIds(
        IReadOnlyList<ProjectPhaseRequest> phases,
        IReadOnlyList<ProjectItemRequest> looseItems,
        ValidationContext<ProjectScopeRequest> context)
    {
        var ids = new HashSet<Guid>();
        void Check(Guid id, string property)
        {
            // Un id vacío ya tiene su propio error.
            if (id != Guid.Empty && !ids.Add(id))
            {
                context.AddFailure(property, "Este identificador se repite en el alcance.");
            }
        }

        for (var phase = 0; phase < phases.Count; phase++)
        {
            if (phases[phase] is not { } value)
            {
                continue;
            }

            Check(value.Id, $"Phases[{phase}].Id");
            for (var item = 0; item < (value.Items?.Count ?? 0); item++)
            {
                if (value.Items![item] is { } phaseItem)
                {
                    Check(phaseItem.Id, $"Phases[{phase}].Items[{item}].Id");
                }
            }
        }

        for (var item = 0; item < looseItems.Count; item++)
        {
            if (looseItems[item] is { } value)
            {
                Check(value.Id, $"Items[{item}].Id");
            }
        }
    }

    private static void CheckMode(
        ProjectScopeRequest scope,
        IReadOnlyList<ProjectPhaseRequest> phases,
        IReadOnlyList<ProjectItemRequest> looseItems,
        ValidationContext<ProjectScopeRequest> context)
    {
        if (string.Equals(scope.PricingMode, nameof(ProjectPricingMode.Global), StringComparison.OrdinalIgnoreCase))
        {
            for (var phase = 0; phase < phases.Count; phase++)
            {
                if (phases[phase]?.Price is not null)
                {
                    context.AddFailure(
                        $"Phases[{phase}].Price", "Con precio global las fases no llevan precio.");
                }
            }

            return;
        }

        if (!string.Equals(scope.PricingMode, nameof(ProjectPricingMode.PerPhase), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (phases.Count == 0 || looseItems.Count > 0)
        {
            context.AddFailure(
                nameof(scope.PricingMode),
                "El precio por fase exige al menos una fase y ningún ítem fuera de las fases.");
        }

        if (scope.GlobalPrice is not null)
        {
            context.AddFailure(nameof(scope.GlobalPrice), "Con precio por fase no hay precio global.");
        }

        // La suma también tiene que caber en la línea. Un precio fuera de rango ya tiene su error, y sumarlo podría
        // desbordar el decimal.
        var prices = phases.Select(phase => phase?.Price ?? 0m).ToArray();
        if (prices.All(price => price is >= 0m and <= ProjectScopeLimits.MaxPrice)
            && prices.Sum() > ProjectScopeLimits.MaxPrice)
        {
            context.AddFailure(
                nameof(scope.Phases),
                $"La suma de las fases no puede pasar de {ProjectScopeLimits.MaxPrice}.");
        }
    }
}
