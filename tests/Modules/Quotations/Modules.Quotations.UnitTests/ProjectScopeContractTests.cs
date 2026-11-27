using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El ida y vuelta del bloque <c>project</c> entre el cuerpo, el dominio y la respuesta (spec 2026-11-26
/// §6.3).</summary>
public sealed class ProjectScopeContractTests
{
    private static readonly Guid Service = Guid.CreateVersion7();
    private static readonly Guid PhaseId = Guid.CreateVersion7();
    private static readonly Guid ItemId = Guid.CreateVersion7();

    [Fact]
    public void TheResponseCarriesTheDocumentCurrencyAndTheTotal()
    {
        var scope = ProjectScope.Create(
            Service,
            ProjectPricingMode.PerPhase,
            null,
            [
                new ProjectPhase(PhaseId, "Descubrimiento", 600_000m, [new ProjectItem(ItemId, "Talleres")]),
                new ProjectPhase(Guid.CreateVersion7(), "Entrega", 400_000m, []),
            ],
            []);

        var dto = ProjectScopeDto.From(scope, "COP");

        Assert.NotNull(dto);
        Assert.Equal("COP", dto.PricesCurrency);
        Assert.Equal(1_000_000m, dto.Total);
        Assert.Equal("PerPhase", dto.PricingMode);
        Assert.Equal(Service, dto.ServiceVariantId);
        Assert.Null(dto.GlobalPrice);
        Assert.Equal([600_000m, 400_000m], dto.Phases.Select(phase => phase.Price!.Value));
        Assert.Equal(PhaseId, dto.Phases[0].Id);
        Assert.Equal(new ProjectItemDto(ItemId, "Talleres"), Assert.Single(dto.Phases[0].Items));
        Assert.Empty(dto.Items);
        Assert.Null(ProjectScopeDto.From(null, "COP"));
    }

    // La forma de §6.3 es la del record con la convención camelCase de la API.
    [Fact]
    public void TheResponseSerializesWithTheContractNames()
    {
        var json = JsonSerializer.Serialize(
            ProjectScopeDto.From(ProjectScope.Empty, "USD"), JsonSerializerOptions.Web);

        Assert.Equal(
            """{"serviceVariantId":null,"pricingMode":"Global","globalPrice":null,"phases":[],"items":[],"pricesCurrency":"USD","total":null}""",
            json);
    }

    // Las listas ausentes son vacías, y el modo se lee sin distinguir mayúsculas.
    [Fact]
    public void TheRequestBecomesTheDomainScope()
    {
        var empty = new ProjectScopeRequest(null, "global", null, null, null, null).ToDomain();
        var full = new ProjectScopeRequest(
            Service, "Global", 10m,
            [new ProjectPhaseRequest(PhaseId, "Descubrimiento", null, null)],
            [new ProjectItemRequest(ItemId, "Soporte")],
            "COP").ToDomain();

        Assert.Equal(ProjectScope.Empty, empty);
        Assert.Equal(
            ProjectScope.Create(
                Service, ProjectPricingMode.Global, 10m,
                [new ProjectPhase(PhaseId, "Descubrimiento", null, [])],
                [new ProjectItem(ItemId, "Soporte")]),
            full);
    }
}
