using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las reglas de campo del bloque <c>project</c> (spec 2026-11-26 §6.3, §12-E/F/G), ejercidas por el guardado: el
/// prefijo <c>Project.</c> y los índices son lo que la pantalla usa para marcar el input.
/// </summary>
public sealed class ProjectScopeRequestValidatorTests
{
    private const decimal TooMuch = 1_000_000_000_000m;

    private static string[] Errors(ProjectScopeRequest project) =>
        [.. new SaveQuotationValidator().Validate(Command(project)).Errors.Select(error => error.PropertyName)];

    private static SaveQuotationCommand Command(ProjectScopeRequest project) =>
        new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1, null, null, null, null, null, [], null, false,
            Project: project);

    private static ProjectScopeRequest Scope(
        string? mode = "Global",
        decimal? globalPrice = null,
        IReadOnlyList<ProjectPhaseRequest>? phases = null,
        IReadOnlyList<ProjectItemRequest>? items = null,
        string? currency = "COP",
        Guid? service = null) =>
        new(service, mode, globalPrice, phases, items, currency);

    private static ProjectItemRequest Item(string? text = "Talleres", Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), text);

    private static ProjectPhaseRequest Phase(
        string? name = "Descubrimiento",
        decimal? price = null,
        IReadOnlyList<ProjectItemRequest>? items = null,
        Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), name, price, items);

    [Fact]
    public void AnEmptyScopeAndAFullOneAreValid()
    {
        Assert.Empty(Errors(Scope(currency: null)));
        Assert.Empty(Errors(Scope(
            mode: "PerPhase", phases: [Phase(price: 10m, items: [Item()])], service: Guid.CreateVersion7())));
        Assert.Empty(Errors(Scope(globalPrice: 10m, phases: [Phase()], items: [Item()])));
    }

    [Fact]
    public void PhaseNameAndItemTextErrorsAreKeyedByPosition()
    {
        var errors = Errors(Scope(
            phases: [Phase(name: " ", items: [Item(), Item(text: null)])],
            items: [Item(text: new string('a', 2001))]));

        Assert.Contains("Project.Phases[0].Name", errors);
        Assert.Contains("Project.Items[0].Text", errors);
        Assert.Contains("Project.Phases[0].Items[1].Text", errors);
        Assert.Equal(3, errors.Length);
        Assert.Contains("Project.Phases[0].Name", Errors(Scope(phases: [Phase(name: new string('a', 201))])));
        Assert.Empty(Errors(Scope(
            phases: [Phase(name: new string('a', 200))], items: [Item(text: new string('a', 2000))])));
    }

    [Fact]
    public void TheItemLimitIsReportedUnderProjectItemsAlsoPerPhase()
    {
        ProjectPhaseRequest[] phases =
        [
            .. Enumerable.Range(0, 3).Select(_ => Phase(items: [.. Enumerable.Range(0, 101).Select(_ => Item())])),
        ];

        Assert.Equal(["Project.Items"], Errors(Scope(mode: "PerPhase", phases: phases)));
        Assert.Equal(["Project.Phases"], Errors(Scope(phases: [.. Enumerable.Range(0, 31).Select(_ => Phase())])));
        Assert.Empty(Errors(Scope(
            phases: [.. Enumerable.Range(0, 30).Select(_ => Phase(items: [.. Enumerable.Range(0, 10).Select(_ => Item())]))])));
    }

    // §12-F: la vista previa manda «-» en lo que todavía está vacío, y es un texto como cualquier otro.
    [Fact]
    public void ADashIsAValidText() =>
        Assert.Empty(Errors(Scope(phases: [Phase(name: "-", items: [Item(text: "-")])], items: [Item(text: "-")])));

    [Fact]
    public void PricesNeedTheirCurrency()
    {
        Assert.Equal(["Project.PricesCurrency"], Errors(Scope(globalPrice: 1m, currency: null)));
        Assert.Equal(
            ["Project.PricesCurrency"], Errors(Scope(mode: "PerPhase", phases: [Phase(price: 1m)], currency: " ")));
        Assert.Empty(Errors(Scope(currency: null)));
    }

    [Fact]
    public void ModeRules()
    {
        Assert.Equal(["Project.PricingMode"], Errors(Scope(mode: "Hourly")));
        Assert.Equal(["Project.PricingMode"], Errors(Scope(mode: null)));
        Assert.Equal(["Project.PricingMode"], Errors(Scope(mode: "1")));
        Assert.Equal(["Project.PricingMode"], Errors(Scope(mode: "PerPhase")));
        Assert.Equal(["Project.PricingMode"], Errors(Scope(mode: "PerPhase", phases: [Phase()], items: [Item()])));
        Assert.Equal(["Project.GlobalPrice"], Errors(Scope(mode: "PerPhase", globalPrice: 1m, phases: [Phase()])));
        Assert.Equal(["Project.Phases[0].Price"], Errors(Scope(phases: [Phase(price: 1m)])));
        Assert.Equal(["Project.GlobalPrice"], Errors(Scope(globalPrice: -1m)));
        Assert.Equal(["Project.Phases[0].Price"], Errors(Scope(mode: "PerPhase", phases: [Phase(price: -1m)])));
    }

    [Fact]
    public void DuplicateIdsAreKeyedOnTheRepeatedId()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(["Project.Items[0].Id"], Errors(Scope(phases: [Phase(id: id)], items: [Item(id: id)])));
        Assert.Equal(["Project.Phases[1].Id"], Errors(Scope(phases: [Phase(id: id), Phase(id: id)])));
        Assert.Equal(["Project.Phases[0].Items[0].Id"], Errors(Scope(phases: [Phase(id: id, items: [Item(id: id)])])));
    }

    [Fact]
    public void AnEmptyIdOrServiceIsAFieldError()
    {
        Assert.Equal(["Project.ServiceVariantId"], Errors(Scope(service: Guid.Empty)));
        Assert.Equal(["Project.Phases[0].Id"], Errors(Scope(phases: [Phase(id: Guid.Empty)])));
        Assert.Equal(["Project.Items[0].Id"], Errors(Scope(items: [Item(id: Guid.Empty)])));
    }

    // Un `[null]` en el JSON es un 422, no una excepción.
    [Fact]
    public void ANullElementIsAFieldError()
    {
        Assert.Contains("Project.Phases", Errors(Scope(phases: [null!])));
        Assert.Contains("Project.Items", Errors(Scope(items: [null!])));
        Assert.Contains("Project.Items", Errors(Scope(phases: [Phase(items: [null!])])));
    }

    // quotation_items.unit_price y los totales son numeric(14,2): por encima la base respondería 500.
    [Fact]
    public void APriceAboveTheColumnIsAFieldError()
    {
        Assert.Equal(["Project.GlobalPrice"], Errors(Scope(globalPrice: TooMuch)));
        Assert.Equal(["Project.GlobalPrice"], Errors(Scope(globalPrice: decimal.MaxValue)));
        Assert.Equal(["Project.Phases[0].Price"], Errors(Scope(mode: "PerPhase", phases: [Phase(price: TooMuch)])));
        Assert.Equal(
            ["Project.Phases[0].Price", "Project.Phases[1].Price"],
            Errors(Scope(mode: "PerPhase", phases: [Phase(price: decimal.MaxValue), Phase(price: decimal.MaxValue)])));
        Assert.Empty(Errors(Scope(globalPrice: ProjectScopeLimits.MaxPrice)));
    }

    [Fact]
    public void ASumOfPhasePricesAboveTheColumnIsAFieldErrorOnThePhases()
    {
        Assert.Equal(
            ["Project.Phases"],
            Errors(Scope(mode: "PerPhase", phases: [Phase(price: ProjectScopeLimits.MaxPrice), Phase(price: 0.01m)])));
        // Con una fase todavía sin precio, lo ya escrito tampoco puede pasar del tope.
        Assert.Equal(
            ["Project.Phases"],
            Errors(Scope(
                mode: "PerPhase",
                phases: [Phase(price: ProjectScopeLimits.MaxPrice), Phase(price: 1m), Phase()])));
        Assert.Empty(Errors(Scope(
            mode: "PerPhase",
            phases: [Phase(price: ProjectScopeLimits.MaxPrice - 1m), Phase(price: 1m)])));
    }

    [Fact]
    public async Task AnUnknownKindOrAProjectOnAStandardCreateIsAFieldError()
    {
        var project = Scope(currency: null);

        Assert.Equal(["Kind"], await CreationErrors("Budget", null));
        Assert.Equal(["Kind"], await CreationErrors("", null));
        Assert.Equal(["Kind"], await CreationErrors("1", project));
        Assert.Equal(["Project"], await CreationErrors(null, project));
        Assert.Equal(["Project"], await CreationErrors("Standard", project));
        Assert.Empty(await CreationErrors(null, null));
        Assert.Empty(await CreationErrors("standard", null));
        Assert.Empty(await CreationErrors("project", project));
        Assert.Empty(await CreationErrors("Project", null));
    }

    // La creación y su vista previa comparten las reglas.
    private static async Task<string[]> CreationErrors(string? kind, ProjectScopeRequest? project)
    {
        var token = TestContext.Current.CancellationToken;
        var create = await new CreateQuotationValidator().ValidateAsync(
            new CreateQuotationCommand(
                Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, null, null, Kind: kind,
                Project: project),
            token);
        var preview = await new PreviewNewQuotationValidator().ValidateAsync(
            new PreviewNewQuotationQuery(
                Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, null, null, [], null, false,
                Kind: kind, Project: project),
            token);
        string[] errors = [.. create.Errors.Select(error => error.PropertyName)];
        Assert.Equal(errors, preview.Errors.Select(error => error.PropertyName));

        return errors;
    }
}
