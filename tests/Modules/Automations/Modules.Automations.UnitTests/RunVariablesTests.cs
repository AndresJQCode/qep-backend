using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

public sealed class RunVariablesTests
{
    [Fact]
    public void SetAndGetRoundTripThroughJson()
    {
        var variables = RunVariables.Empty();
        variables.Set("cedula", "123");
        variables.Set("cliente", JsonNode.Parse("""{ "id": 7, "firstName": "Ana" }"""));

        var again = RunVariables.Parse(variables.ToJson());

        Assert.Equal(7, again.Get("cliente.id")!.GetValue<int>());
        Assert.Equal("123", again.TextOf("cedula"));
        Assert.True(again.Has("cliente.firstName"));
        Assert.False(again.Has("cliente.nope"));
    }

    [Fact]
    public void TextOfFormatsScalarsInvariantly()
    {
        var variables = RunVariables.Empty();
        variables.Set("monto", JsonValue.Create(1234.5));
        variables.Set("activo", JsonValue.Create(true));
        variables.Set("nada", null);

        Assert.Equal("1234.5", variables.TextOf("monto"));
        Assert.Equal("true", variables.TextOf("activo"));
        Assert.Equal("", variables.TextOf("nada"));
        Assert.Equal("", variables.TextOf("ausente"));
    }

    [Fact]
    public void SetReplacesTheWholeRootVariable()
    {
        var variables = RunVariables.Empty();
        variables.Set("cliente", JsonNode.Parse("""{ "id": 7 }"""));
        variables.Set("cliente", JsonValue.Create("otro"));

        Assert.Equal("otro", variables.TextOf("cliente"));
        Assert.False(variables.Has("cliente.id"));
    }

    [Fact]
    public void CloneIsIndependentFromTheOriginal()
    {
        var variables = RunVariables.Empty();
        variables.Set("a", "1");
        var copy = variables.Clone();
        copy.Set("a", "2");

        Assert.Equal("1", variables.TextOf("a"));
        Assert.Equal("1", variables.Snapshot()["a"]!.GetValue<string>());
    }

    [Fact]
    public void ParseOfEmptyTextIsEmptyAndOfANonObjectFails()
    {
        Assert.Equal("{}", RunVariables.Parse("").ToJson());
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RunVariables.Parse("[1]"));
    }
}
