using System.Text.Json.Nodes;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.UnitTests;

public sealed class JsonPathTests
{
    private static readonly JsonNode Root = JsonNode.Parse("""{ "data": [{ "id": 7 }], "cliente": { "firstName": "Ana" } }""")!;

    [Fact]
    public void SelectsNestedAndIndexed()
    {
        Assert.Equal(7, JsonPath.Select(Root, "data[0].id")!.GetValue<int>());
        Assert.Equal("Ana", JsonPath.Select(Root, "cliente.firstName")!.GetValue<string>());
    }

    [Fact]
    public void AMissingSegmentIsNull()
    {
        Assert.Null(JsonPath.Select(Root, "data[3].id"));
        Assert.Null(JsonPath.Select(Root, "nope.x"));
        Assert.Null(JsonPath.Select(Root, "cliente.firstName.x"));
    }

    [Theory]
    [InlineData("1a")]
    [InlineData("a..b")]
    [InlineData("a[x]")]
    [InlineData("")]
    public void InvalidPathsAreRejected(string path)
    {
        Assert.False(JsonPath.IsValid(path));
        Assert.Null(JsonPath.Select(Root, path));
    }

    [Fact]
    public void ValidPathsAreAccepted()
    {
        Assert.True(JsonPath.IsValid("a"));
        Assert.True(JsonPath.IsValid("_a.b_1[0][2].c"));
    }

    // Ruling P2-R36: la respuesta de una API puede ser un arreglo en la raíz (Anexo C: all-invoice-items, failures, departments…).
    private static readonly JsonNode RootArray = JsonNode.Parse("""[{ "id": 1, "referenceCode": "CAL-001" }, { "id": 2 }]""")!;

    [Fact]
    public void ADollarIsTheWholeResponse()
    {
        Assert.True(JsonNode.DeepEquals(RootArray, JsonPath.SelectResponse(RootArray, "$")));
        Assert.True(JsonNode.DeepEquals(Root, JsonPath.SelectResponse(Root, "$")));
    }

    [Theory]
    [InlineData("[0].id")]
    [InlineData("$[0].id")]
    public void AResponsePathCanStartWithAnIndex(string path) =>
        Assert.Equal(1, JsonPath.SelectResponse(RootArray, path)!.GetValue<int>());

    [Fact]
    public void ADollarPrefixReadsLikeThePlainPath()
    {
        Assert.True(JsonNode.DeepEquals(JsonPath.SelectResponse(Root, "data[0]"), JsonPath.SelectResponse(Root, "$.data[0]")));
        Assert.Equal(7, JsonPath.SelectResponse(Root, "$.data[0].id")!.GetValue<int>());
        Assert.Equal(7, JsonPath.SelectResponse(Root, "data[0].id")!.GetValue<int>());
        Assert.Null(JsonPath.SelectResponse(RootArray, "$[5]"));
    }

    [Theory]
    [InlineData("$$")]
    [InlineData("[x]")]
    [InlineData("$data")]
    [InlineData("$.")]
    [InlineData(".data")]
    [InlineData("data.$")]
    [InlineData("")]
    public void InvalidResponsePathsAreRejected(string path)
    {
        Assert.False(JsonPath.IsValidResponsePath(path));
        Assert.Null(JsonPath.SelectResponse(Root, path));
    }

    /// <summary>Las referencias a variables (<c>{cliente.id}</c>, Condición) siguen con la forma de siempre: <c>$</c> no es una variable.</summary>
    [Theory]
    [InlineData("$")]
    [InlineData("[0].id")]
    [InlineData("$.data")]
    public void AVariablePathDoesNotTakeTheResponseForms(string path) => Assert.False(JsonPath.IsValid(path));
}
