using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.UnitTests;

public sealed class TemplateTextTests
{
    private static RunVariables Variables()
    {
        var variables = RunVariables.Empty();
        variables.Set("cliente", JsonNode.Parse("""{ "id": 7, "firstName": "Ana" }"""));
        variables.Set("cedula", "123");
        return variables;
    }

    [Fact]
    public void InterpolatesNestedVariables() =>
        Assert.Equal("Hola Ana, tu cédula es 123", TemplateText.Interpolate("Hola {cliente.firstName}, tu cédula es {cedula}", Variables()));

    [Fact]
    public void AMissingVariableIsEmpty() => Assert.Equal("", TemplateText.Interpolate("{nada}", Variables()));

    [Fact]
    public void AnObjectVariableInterpolatesAsCompactJson() =>
        Assert.Equal("{\"id\":7,\"firstName\":\"Ana\"}", TemplateText.Interpolate("{cliente}", Variables()));

    [Fact]
    public void ABraceThatIsNotAPathStaysLiteral() =>
        Assert.Equal("{ \"customerId\": 7 }", TemplateText.Interpolate("{ \"customerId\": {cliente.id} }", Variables()));

    [Fact]
    public void ReferencesInListsEveryPath() =>
        Assert.Equal(["cliente.firstName", "cedula"], TemplateText.ReferencesIn("Hola {cliente.firstName}, tu cédula es {cedula}"));
}
