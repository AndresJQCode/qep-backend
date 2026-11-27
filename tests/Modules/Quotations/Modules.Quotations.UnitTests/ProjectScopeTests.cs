using BuildingBlocks.Domain.Pricing;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El alcance de una cotización de proyecto (spec 2026-11-26 §6.3): fases e ítems con ids estables, precio global o por
/// fase, y los topes aprobados (P6). La red del dominio: el 422 de campo lo da el validador de la aplicación antes.
/// </summary>
public sealed class ProjectScopeTests
{
    private static readonly Guid ServiceVariantId = Guid.CreateVersion7();

    [Fact]
    public void AnEmptyScopeIsGlobalWithoutServiceOrContent()
    {
        var scope = ProjectScope.Empty;

        Assert.Equal(ProjectPricingMode.Global, scope.PricingMode);
        Assert.Null(scope.ServiceVariantId);
        Assert.Null(scope.GlobalPrice);
        Assert.Empty(scope.Phases);
        Assert.Empty(scope.Items);
        Assert.Null(scope.Total);
        Assert.False(scope.IsComplete);
        Assert.False(scope.HasPrices);
        Assert.Equal(0, scope.ItemCount);
    }

    [Fact]
    public void GlobalTotalIsTheGlobalPrice()
    {
        var scope = ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, 1000m, [Phase(price: null, Item())], [Item()]);

        Assert.Equal(1000m, scope.Total);
        Assert.True(scope.IsComplete);
        Assert.True(scope.HasPrices);
        Assert.Equal(2, scope.ItemCount);
    }

    [Fact]
    public void PerPhaseTotalIsTheSumOrNullWhileAPriceIsMissing()
    {
        var complete = PerPhase(Phase(600m), Phase(400m));
        var missing = PerPhase(Phase(600m), Phase(price: null));

        Assert.Equal(1000m, complete.Total);
        Assert.True(complete.IsComplete);
        Assert.Null(missing.Total);
        Assert.False(missing.IsComplete);
        Assert.True(missing.HasPrices);
    }

    [Fact]
    public void WithoutAServiceTheScopeIsNotComplete()
    {
        var scope = ProjectScope.Create(null, ProjectPricingMode.Global, 1000m, [], []);

        Assert.Equal(1000m, scope.Total);
        Assert.False(scope.IsComplete);
    }

    [Fact]
    public void PerPhaseNeedsAPhaseAndNoLooseItems()
    {
        Assert.Throws<ArgumentException>(() => PerPhase());
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.PerPhase, null, [Phase(100m)], [Item()]));
    }

    [Fact]
    public void GlobalRejectsPhasePricesAndPerPhaseRejectsAGlobalPrice()
    {
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, 1000m, [Phase(100m)], []));
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.PerPhase, 1000m, [Phase(100m)], []));
    }

    [Fact]
    public void IdsAreUniqueAcrossPhasesAndItems()
    {
        var shared = Guid.CreateVersion7();

        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, null,
            [new ProjectPhase(shared, "Descubrimiento", null, [])], [new ProjectItem(shared, "Soporte")]));
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, null,
            [new ProjectPhase(Guid.CreateVersion7(), "Descubrimiento", null, [new ProjectItem(shared, "Talleres")])],
            [new ProjectItem(shared, "Soporte")]));
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, null,
            [new ProjectPhase(shared, "Uno", null, []), new ProjectPhase(shared, "Dos", null, [])], []));
    }

    [Fact]
    public void LimitsArePinned()
    {
        Assert.Equal(30, ProjectScopeLimits.Phases);
        Assert.Equal(300, ProjectScopeLimits.Items);
        Assert.Equal(200, ProjectScopeLimits.PhaseNameMaxLength);
        Assert.Equal(2000, ProjectScopeLimits.ItemTextMaxLength);

        // En el límite, todo se acepta.
        var phases = Enumerable.Range(0, 30)
            .Select(_ => Phase(1m, Enumerable.Range(0, 10).Select(_ => Item()).ToArray()))
            .ToArray();
        Assert.Equal(300, PerPhase(phases).ItemCount);
        Assert.NotNull(Global(
            [new ProjectPhase(Guid.CreateVersion7(), new string('a', 200), null, [])],
            [new ProjectItem(Guid.CreateVersion7(), new string('a', 2000))]));

        Assert.Throws<ArgumentException>(() => PerPhase(Enumerable.Range(0, 31).Select(_ => Phase(1m)).ToArray()));
        Assert.Throws<ArgumentException>(() => Global([], Enumerable.Range(0, 301).Select(_ => Item()).ToArray()));
        // Repartidos en fases: el tope cuenta los de las fases más los sueltos (spec §12-E).
        Assert.Throws<ArgumentException>(() => PerPhase(
            Phase(1m, Enumerable.Range(0, 200).Select(_ => Item()).ToArray()),
            Phase(1m, Enumerable.Range(0, 101).Select(_ => Item()).ToArray())));
        Assert.Throws<ArgumentException>(() => Global(
            [new ProjectPhase(Guid.CreateVersion7(), new string('a', 201), null, [])], []));
        Assert.Throws<ArgumentException>(() => Global(
            [new ProjectPhase(Guid.CreateVersion7(), "  ", null, [])], []));
        Assert.Throws<ArgumentException>(() => Global(
            [], [new ProjectItem(Guid.CreateVersion7(), new string('a', 2001))]));
        Assert.Throws<ArgumentException>(() => Global([], [new ProjectItem(Guid.CreateVersion7(), "")]));
        Assert.Throws<ArgumentException>(() => Global([], [new ProjectItem(Guid.CreateVersion7(), "  ")]));
        Assert.Throws<ArgumentException>(() => PerPhase(Phase(1m, new ProjectItem(Guid.CreateVersion7(), " "))));

        // La vista previa manda "-" en un texto todavía vacío (spec §12-F).
        Assert.NotNull(Global(
            [new ProjectPhase(Guid.CreateVersion7(), "-", null, [])], [new ProjectItem(Guid.CreateVersion7(), "-")]));
    }

    [Fact]
    public void NegativePricesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, -1m, [], []));
        Assert.Throws<ArgumentException>(() => PerPhase(Phase(-0.01m)));
        Assert.Equal(0m, PerPhase(Phase(0m)).Total);
    }

    [Fact]
    public void AnUnknownPricingModeIsRejected() =>
        Assert.Throws<ArgumentException>(() => ProjectScope.Create(
            ServiceVariantId, (ProjectPricingMode)7, null, [], []));

    [Fact]
    public void LaterChangesToTheSourceListsDoNotChangeTheScope()
    {
        var items = new List<ProjectItem> { Item() };
        var phases = new List<ProjectPhase> { new(Guid.CreateVersion7(), "Descubrimiento", null, items) };
        var scope = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, null, phases, []);

        items.Add(Item());
        phases.Add(Phase(price: null));

        Assert.Single(scope.Phases);
        Assert.Single(scope.Phases[0].Items);
    }

    [Fact]
    public void RoundingAppliesToEachPhasePrice()
    {
        var scope = PerPhase(Phase(100.4m), Phase(200.4m));

        var rounded = scope.RoundedWith(new PriceRounding(0, PriceRoundingMode.Nearest));

        Assert.Equal([100m, 200m], rounded.Phases.Select(phase => phase.Price!.Value));
        Assert.Equal(300m, rounded.Total);
        Assert.Equal(scope.Phases.Select(phase => phase.Id), rounded.Phases.Select(phase => phase.Id));
    }

    [Fact]
    public void RoundingAppliesToTheGlobalPrice()
    {
        var scope = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 100.5m, [], []);

        Assert.Equal(101m, scope.RoundedWith(new PriceRounding(0, PriceRoundingMode.Nearest)).GlobalPrice);
    }

    [Fact]
    public void WithoutPricesClearsEveryPriceAndKeepsTheStructure()
    {
        var scope = PerPhase(Phase(600m, Item()), Phase(400m));

        var cleared = scope.WithoutPrices();

        Assert.False(cleared.HasPrices);
        Assert.Null(cleared.Total);
        Assert.Equal(1, cleared.ItemCount);
        Assert.Equal(scope.ServiceVariantId, cleared.ServiceVariantId);
        Assert.Equal(scope.Phases.Select(phase => phase.Id), cleared.Phases.Select(phase => phase.Id));
        Assert.False(ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 5m, [], []).WithoutPrices().HasPrices);
    }

    [Fact]
    public void WithPricesOfMatchesPhasesById()
    {
        var kept = Phase(500m);
        var saved = PerPhase(kept);
        var typed = PerPhase(kept with { Price = 999m }, Phase(300m));

        var result = typed.WithPricesOf(saved);

        Assert.Equal(500m, result.Phases[0].Price);
        Assert.Null(result.Phases[1].Price);
        Assert.Null(result.Total);
    }

    // Spec §12-H: si el modo cambió, los precios del otro modo no se heredan.
    [Fact]
    public void WithPricesOfLeavesTheOtherModeEmpty()
    {
        var phase = Phase(price: null);
        var savedGlobal = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 1000m, [phase], []);
        var savedPerPhase = PerPhase(phase with { Price = 600m });

        var toPerPhase = PerPhase(phase with { Price = 1m }).WithPricesOf(savedGlobal);
        var toGlobal = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 1m, [phase], [])
            .WithPricesOf(savedPerPhase);

        Assert.False(toPerPhase.HasPrices);
        Assert.False(toGlobal.HasPrices);
    }

    [Fact]
    public void WithPricesOfKeepsTheSavedGlobalPrice()
    {
        var saved = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 1000m, [], []);
        var typed = ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, 7m, [], [Item()]);

        var result = typed.WithPricesOf(saved);

        Assert.Equal(1000m, result.GlobalPrice);
        Assert.Single(result.Items);
    }

    [Fact]
    public void TwoScopesWithTheSameContentAreEqual()
    {
        var phaseId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var looseId = Guid.CreateVersion7();

        ProjectScope Build(string text) => ProjectScope.Create(
            ServiceVariantId, ProjectPricingMode.Global, 1000m,
            [new ProjectPhase(phaseId, "Descubrimiento", null, [new ProjectItem(itemId, text)])],
            [new ProjectItem(looseId, "Soporte")]);

        Assert.Equal(Build("Talleres"), Build("Talleres"));
        Assert.Equal(Build("Talleres").GetHashCode(), Build("Talleres").GetHashCode());
        Assert.NotEqual(Build("Talleres"), Build("Entrevistas"));
    }

    private static ProjectScope Global(IReadOnlyList<ProjectPhase> phases, IReadOnlyList<ProjectItem> items) =>
        ProjectScope.Create(ServiceVariantId, ProjectPricingMode.Global, null, phases, items);

    private static ProjectScope PerPhase(params ProjectPhase[] phases) =>
        ProjectScope.Create(ServiceVariantId, ProjectPricingMode.PerPhase, null, phases, []);

    private static ProjectPhase Phase(decimal? price, params ProjectItem[] items) =>
        new(Guid.CreateVersion7(), "Descubrimiento", price, items);

    private static ProjectItem Item() => new(Guid.CreateVersion7(), "Talleres con el equipo de ventas");
}
