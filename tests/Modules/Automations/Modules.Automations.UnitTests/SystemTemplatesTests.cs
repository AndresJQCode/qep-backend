using System.Text.RegularExpressions;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Infrastructure.Seed;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-21 §4.5 y D-C8: las plantillas del sistema son los JSON embebidos en Infrastructure, y cada una se puede
/// instalar y publicar en un chatbot de WhatsApp sin errores.</summary>
public sealed partial class SystemTemplatesTests
{
    private static readonly SystemTemplateCatalog Catalog = new();

    // WhatsApp, sin «API externa» (D-C14: una plantilla no referencia conexiones) y con horario en todas las conexiones.
    private static FlowValidationContext Ctx() =>
        new(new HashSet<string> { "WhatsApp" }, new HashSet<Guid>(), new HashSet<Guid>(), AllConnectionsHaveBusinessHours: true);

    [Fact]
    public void EverySystemTemplateParsesAndValidatesWithoutErrors()
    {
        Assert.NotEmpty(Catalog.All);
        foreach (var template in Catalog.All)
        {
            var raw = FlowValidator.Validate(FlowJson.Parse(template.FlowJson), Ctx());
            var installedJson = TemplateParameters.Apply(template.FlowJson, TemplateParameters.Resolve(template.Parameters, null));
            var installed = FlowValidator.Validate(FlowJson.Parse(installedJson), Ctx());

            Assert.DoesNotContain(raw, issue => issue.Severity == FlowIssueSeverity.Error);
            Assert.DoesNotContain(installed, issue => issue.Severity == FlowIssueSeverity.Error);
            Assert.DoesNotContain(installed, issue => issue.Code == FlowIssueCodes.BusinessHoursNotAssigned);
            Assert.DoesNotContain("{param.", installedJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SystemTemplatesHaveStableIds()
    {
        Assert.Equal(
            ["outside-hours", "welcome-menu-handoff"],
            Catalog.All.Select(template => template.Id).Order(StringComparer.Ordinal));
        Assert.Equal("welcome-menu-handoff", Catalog.Find("welcome-menu-handoff")!.Id);
        Assert.Null(Catalog.Find("no-existe"));
    }

    // D-C8: exactamente las dos de la primera entrega, con su número de bloques contado al cargar.
    [Fact]
    public void TheCatalogHasExactlyTheTwoTemplatesWithTheirBlockCounts()
    {
        Assert.Equal(
            [("outside-hours", 4), ("welcome-menu-handoff", 4)],
            Catalog.All.Select(template => (template.Id, template.BlockCount)));
        Assert.All(Catalog.All, template => Assert.Equal(FlowJson.Parse(template.FlowJson).Nodes.Count, template.BlockCount));
    }

    // Un cambio en el glob del csproj o en el espacio de nombres no puede dejar la galería vacía en silencio: el host no arranca.
    [Fact]
    public void ACatalogWithoutTemplateResourcesDoesNotStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SystemTemplateCatalog(typeof(SystemTemplatesTests).Assembly));

        Assert.Contains("no trae plantillas del sistema", exception.Message, StringComparison.Ordinal);
    }

    // La tarjeta de la galería (§4.4) pinta preview y parámetros: tienen que decir la verdad del flujo.
    [Fact]
    public void PreviewIsTheBlockTitlesAndEveryParameterIsDeclared()
    {
        foreach (var template in Catalog.All)
        {
            var flow = FlowJson.Parse(template.FlowJson);
            var used = ParamToken().Matches(template.FlowJson).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(flow.Nodes.Select(node => node.Title), template.Preview);
            Assert.Equal(used.Order(StringComparer.Ordinal), template.Parameters.Select(parameter => parameter.Key).Order(StringComparer.Ordinal));
            Assert.All(template.Parameters, parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.DefaultValue)));
            Assert.Equal(["WhatsApp"], template.Channels);
            Assert.Equal("Support", template.Category);
        }

        Assert.Equal(["greeting", "option1", "option2"], Catalog.Find("welcome-menu-handoff")!.Parameters.Select(parameter => parameter.Key));
        Assert.Null(Catalog.Find("welcome-menu-handoff")!.Requirement);
        Assert.Equal(["closedMessage"], Catalog.Find("outside-hours")!.Parameters.Select(parameter => parameter.Key));
        Assert.Equal("usa el horario de la conexión", Catalog.Find("outside-hours")!.Requirement);
    }

    // Spec 2026-10-24 P-2 (decisión del owner): en «Fuera de horario» el cliente no recibe «Te paso con un asesor.»; la conversación
    // sigue natural y el asesor la toma. Sólo esa plantilla: la de bienvenida conserva su texto.
    [Fact]
    public void TheOutsideHoursHandoffDoesNotTellTheCustomer()
    {
        var handoff = Assert.Single(FlowJson.Parse(Catalog.Find("outside-hours")!.FlowJson).Nodes.OfType<HandoffNode>());

        Assert.Null(handoff.Text);
    }

    [GeneratedRegex(@"\{param\.(?<key>[a-zA-Z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParamToken();
}
