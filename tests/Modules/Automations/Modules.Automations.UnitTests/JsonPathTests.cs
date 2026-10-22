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
}
