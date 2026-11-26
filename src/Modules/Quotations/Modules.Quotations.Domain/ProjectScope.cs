using System.Text.Json.Serialization;
using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>El tipo de documento (spec 2026-11-26 §5): se fija al crear la cotización, no cambia y el pedido lo copia.
/// Se guarda como texto.</summary>
public enum QuotationKind
{
    Standard,
    Project
}

/// <summary>Cómo se precia un proyecto: un precio por todo, o uno por fase (spec 2026-11-26 §6.3).</summary>
public enum ProjectPricingMode
{
    Global,
    PerPhase
}

/// <summary>Un ítem del alcance: texto libre, sin precio propio. El <paramref name="Id"/> lo genera la pantalla y el
/// backend nunca lo cambia: la gestión de proyectos lo va a usar para seguir el mismo ítem.</summary>
public sealed record ProjectItem(Guid Id, string Text);

/// <summary>Una fase del alcance, con sus ítems. <paramref name="Price"/> sólo tiene valor en
/// <see cref="ProjectPricingMode.PerPhase"/>.</summary>
public sealed record ProjectPhase(Guid Id, string Name, decimal? Price, IReadOnlyList<ProjectItem> Items)
{
    // Un record compara las listas por referencia: dos fases con los mismos ítems serían distintas.
    public bool Equals(ProjectPhase? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Price == other.Price
        && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Price, Items.Count);
}

/// <summary>Lo que el catálogo dice del servicio elegido, para armar la línea de la cotización
/// (<see cref="Quotation.ReplaceProjectScope"/>): se congela en la línea como en cualquier otra.</summary>
public sealed record ProjectServiceLine(
    Guid VariantId,
    Guid ProductId,
    int TaxPercentage,
    ProductKind Kind,
    SalesUnit SalesUnit);

/// <summary>Los topes del alcance (spec 2026-11-26, P6). El de ítems cuenta los de las fases más los sueltos.</summary>
public static class ProjectScopeLimits
{
    public const int Phases = 30;
    public const int Items = 300;
    public const int PhaseNameMaxLength = 200;
    public const int ItemTextMaxLength = 2000;
}

/// <summary>
/// El alcance y el precio de un documento de proyecto (spec 2026-11-26 §5 y §6.3). Es una foto que viaja entera: se
/// guarda como <c>jsonb</c>, la cotización la reemplaza completa y el pedido la copia con los mismos ids. La moneda es
/// la del documento y <see cref="Total"/> se deriva: ninguno de los dos se guarda.
///
/// <see cref="Create"/> es el punto de entrada y hace cumplir las reglas. El 422 de cada campo lo da antes el validador
/// de la aplicación; esto es la red del dominio y por eso lanza <see cref="ArgumentException"/>, sin código de contrato.
/// El constructor del record queda para leer lo ya guardado.
/// </summary>
public sealed record ProjectScope(
    Guid? ServiceVariantId,
    ProjectPricingMode PricingMode,
    decimal? GlobalPrice,
    IReadOnlyList<ProjectPhase> Phases,
    IReadOnlyList<ProjectItem> Items)
{
    /// <summary>Con lo que nace una cotización de proyecto: sin servicio, sin precio y sin contenido.</summary>
    public static ProjectScope Empty { get; } = new(null, ProjectPricingMode.Global, null, [], []);

    /// <summary>El precio del proyecto: el global, o la suma de las fases; <c>null</c> mientras falte alguno.</summary>
    [JsonIgnore]
    public decimal? Total => PricingMode == ProjectPricingMode.Global
        ? GlobalPrice
        : Phases.Any(phase => phase.Price is null) ? null : Phases.Sum(phase => phase.Price);

    /// <summary>Con servicio y precio: lo que exigen enviar y convertir.</summary>
    [JsonIgnore]
    public bool IsComplete => ServiceVariantId is not null && Total is not null;

    [JsonIgnore]
    public bool HasPrices => GlobalPrice is not null || Phases.Any(phase => phase.Price is not null);

    [JsonIgnore]
    public int ItemCount => Items.Count + Phases.Sum(phase => phase.Items.Count);

    public static ProjectScope Create(
        Guid? serviceVariantId,
        ProjectPricingMode mode,
        decimal? globalPrice,
        IReadOnlyList<ProjectPhase> phases,
        IReadOnlyList<ProjectItem> items)
    {
        ArgumentNullException.ThrowIfNull(phases);
        ArgumentNullException.ThrowIfNull(items);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException($"Unknown project pricing mode '{mode}'.", nameof(mode));
        }

        if (phases.Count > ProjectScopeLimits.Phases)
        {
            throw new ArgumentException(
                $"A project scope has at most {ProjectScopeLimits.Phases} phases.", nameof(phases));
        }

        EnsurePriceIsNotNegative(globalPrice, nameof(globalPrice));
        var ids = new HashSet<Guid>();
        // Copias: el alcance no cambia si quien lo armó sigue tocando sus listas.
        ProjectItem[] looseItems = [.. items];
        EnsureItems(looseItems, ids, nameof(items));
        var copiedPhases = new ProjectPhase[phases.Count];
        for (var index = 0; index < phases.Count; index++)
        {
            var phase = phases[index] ?? throw new ArgumentException("A phase cannot be null.", nameof(phases));
            EnsureText(phase.Name, ProjectScopeLimits.PhaseNameMaxLength, "A phase name", nameof(phases));
            EnsurePriceIsNotNegative(phase.Price, nameof(phases));
            EnsureUnique(phase.Id, ids, nameof(phases));
            ProjectItem[] phaseItems =
                [.. phase.Items ?? throw new ArgumentException("A phase needs its item list.", nameof(phases))];
            EnsureItems(phaseItems, ids, nameof(phases));
            copiedPhases[index] = phase with { Items = phaseItems };
        }

        var scope = new ProjectScope(serviceVariantId, mode, globalPrice, copiedPhases, looseItems);
        if (scope.ItemCount > ProjectScopeLimits.Items)
        {
            throw new ArgumentException(
                $"A project scope has at most {ProjectScopeLimits.Items} items.", nameof(items));
        }

        scope.EnsureMatchesItsMode();
        return scope;
    }

    /// <summary>Cada precio redondeado por separado, para que la suma que se cobra sea la que se lee (P8).</summary>
    public ProjectScope RoundedWith(PriceRounding rounding)
    {
        decimal? Round(decimal? price) => price is { } value ? rounding.Apply(value) : null;

        return this with
        {
            GlobalPrice = Round(GlobalPrice),
            Phases = [.. Phases.Select(phase => phase with { Price = Round(phase.Price) })]
        };
    }

    public ProjectScope WithoutPrices() =>
        this with { GlobalPrice = null, Phases = [.. Phases.Select(phase => phase with { Price = null })] };

    /// <summary>
    /// Esta estructura con los precios de <paramref name="saved"/> (spec §12-H): el global, y cada fase el de la fase
    /// guardada con su mismo id; una fase nueva queda sin precio. Si el modo cambió, los precios del otro modo no se
    /// heredan.
    /// </summary>
    public ProjectScope WithPricesOf(ProjectScope saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var savedPrices = saved.Phases.ToDictionary(phase => phase.Id, phase => phase.Price);
        var perPhase = PricingMode == ProjectPricingMode.PerPhase;
        return this with
        {
            GlobalPrice = perPhase ? null : saved.GlobalPrice,
            Phases =
            [
                .. Phases.Select(phase => phase with
                {
                    Price = perPhase ? savedPrices.GetValueOrDefault(phase.Id) : null
                })
            ]
        };
    }

    // Un record compara las listas por referencia: sin esto, un alcance releído de la base sería siempre «otro».
    public bool Equals(ProjectScope? other) =>
        other is not null
        && ServiceVariantId == other.ServiceVariantId
        && PricingMode == other.PricingMode
        && GlobalPrice == other.GlobalPrice
        && Phases.SequenceEqual(other.Phases)
        && Items.SequenceEqual(other.Items);

    public override int GetHashCode() =>
        HashCode.Combine(ServiceVariantId, PricingMode, GlobalPrice, Phases.Count, Items.Count);

    private void EnsureMatchesItsMode()
    {
        if (PricingMode == ProjectPricingMode.Global)
        {
            if (Phases.Any(phase => phase.Price is not null))
            {
                throw new ArgumentException("A globally priced project has no phase prices.");
            }

            return;
        }

        if (Phases.Count == 0)
        {
            throw new ArgumentException("A project priced per phase needs at least one phase.");
        }

        if (Items.Count > 0)
        {
            throw new ArgumentException("A project priced per phase has no items outside its phases.");
        }

        if (GlobalPrice is not null)
        {
            throw new ArgumentException("A project priced per phase has no global price.");
        }
    }

    private static void EnsureItems(IReadOnlyList<ProjectItem> items, HashSet<Guid> ids, string parameterName)
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("An item cannot be null.", parameterName);
            }

            EnsureText(item.Text, ProjectScopeLimits.ItemTextMaxLength, "An item text", parameterName);
            EnsureUnique(item.Id, ids, parameterName);
        }
    }

    private static void EnsureText(string text, int maxLength, string what, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
        {
            throw new ArgumentException($"{what} takes 1 to {maxLength} characters.", parameterName);
        }
    }

    private static void EnsureUnique(Guid id, HashSet<Guid> ids, string parameterName)
    {
        if (!ids.Add(id))
        {
            throw new ArgumentException($"The id '{id}' repeats within the project scope.", parameterName);
        }
    }

    private static void EnsurePriceIsNotNegative(decimal? price, string parameterName)
    {
        if (price < 0)
        {
            throw new ArgumentException("A project price cannot be negative.", parameterName);
        }
    }
}
