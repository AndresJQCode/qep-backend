using System.Text.Json.Nodes;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class FlowJsonTests
{
    [Fact]
    public void ParsesTheAnnexAFlow()
    {
        var flow = AnnexAFlow.Parse();

        Assert.Equal(1, flow.Schema);
        Assert.Equal("n1", flow.Start.FirstNodeId);
        Assert.Equal([new FlowVariable("cedula", "text"), new FlowVariable("equipos", "list")], flow.Variables);
        Assert.Equal(13, flow.Nodes.Count);

        var search = Assert.IsType<HttpNode>(flow.Nodes.Single(node => node.Id == "n3"));
        Assert.Equal(FlowNodeKinds.Http, search.Kind);
        Assert.Equal("Buscar cliente", search.Title);
        Assert.Equal(AnnexAFlow.CustomersApi, search.ConnectionId);
        Assert.Equal("GET", search.Method);
        Assert.Equal("/customers", search.Path);
        Assert.Equal([new KeyValue("identificationNumber", "{cedula}")], search.Query);
        Assert.Null(search.Body);
        Assert.Equal(8, search.TimeoutSeconds);
        Assert.Equal([200], search.OkWhen!);
        Assert.Equal(new HttpMapping("data[0]", "cliente"), Assert.Single(search.Mapping));
        Assert.NotNull(search.SampleResponse);
        Assert.Equal("n4", search.Outputs["ok"]);
        Assert.Equal("n9", search.Outputs["error"]);

        // n12 viene como en el anexo, sin query, timeout ni mapeo: se leen con sus valores por defecto (D-H4).
        var create = Assert.IsType<HttpNode>(flow.Nodes.Single(node => node.Id == "n12"));
        Assert.Empty(create.Query);
        Assert.Equal(8, create.TimeoutSeconds);
        Assert.Null(create.OkWhen);
        Assert.Empty(create.Mapping);
        Assert.Equal(new HttpBody("json", "{ \"customerId\": {cliente.id}, \"address\": \"{direccion}\" }", null, null, null), create.Body);

        // Los terminales del anexo no traen outputs.
        Assert.Empty(Assert.IsType<HandoffNode>(flow.Nodes.Single(node => node.Id == "n9")).Outputs);
        var list = Assert.IsType<ListNode>(flow.Nodes.Single(node => node.Id == "n5"));
        Assert.Equal(new ListFixedRow("other", "Otra dirección", null), list.Options.ExtraOption);
        Assert.Null(list.Outputs["other"]);
    }

    // Los records llevan listas y un JsonElement, que no comparan por valor, así que la igualdad de records no sirve.
    // Se comprueban las dos mitades de «sin pérdida»: serializar es un punto fijo (lo que sale vuelve a salir igual)
    // y todo lo que traía el JSON original está, con el mismo valor, en lo serializado.
    [Fact]
    public void RoundTripsWithoutLoss()
    {
        var first = FlowJson.Serialize(FlowJson.Parse(AnnexAFlow.Json));
        var second = FlowJson.Serialize(FlowJson.Parse(first));

        Assert.Equal(first, second);
        AssertContains(JsonNode.Parse(AnnexAFlow.Json), JsonNode.Parse(first), "$");
    }

    [Fact]
    public void AnUnknownKindThrowsFlowFormat()
    {
        const string json = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [ { "id": "n1", "kind": "teleport", "title": "Viaje" } ] }
            """;

        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(json));

        Assert.Equal("flow.unknown_kind", exception.Code);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "resolve", "title": "A" }, { "id": "n1", "kind": "resolve", "title": "B" } ] }""")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "http", "title": "A", "connectionId": "…", "method": "GET", "path": "/x" } ] }""")]
    public void AMalformedFlowThrowsFlowFormat(string json)
    {
        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(json));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Fact]
    public void OutputsOfAButtonsNodeAreItsButtonsPlusOther()
    {
        var node = new ButtonsNode(
            "n1", "Motivo", new Dictionary<string, string?>(), "¿Qué necesitas?",
            [new FlowButton("tech", "Servicio técnico"), new FlowButton("maint", "Mantenimiento")], null, "repeat");

        Assert.Equal(["tech", "maint", "other"], FlowNodeKinds.OutputsOf(node));
    }

    [Fact]
    public void OutputsOfAListDependOnItsOptions()
    {
        var fixedRows = new ListOptions("fixed", [new ListFixedRow("a", "A", null)], null, null, null, null, null);
        var dynamicWithExtra = new ListOptions("dynamic", null, "equipos", "id", "name", null, new ListFixedRow("x", "Otra", null));

        Assert.Equal(["chosen", "other"], FlowNodeKinds.OutputsOf(List(fixedRows)));
        Assert.Equal(["chosen", "extra", "tooMany", "other"], FlowNodeKinds.OutputsOf(List(dynamicWithExtra)));
        Assert.Empty(FlowNodeKinds.OutputsOf(new ResolveNode("n1", "Listo", new Dictionary<string, string?>(), null, null)));
        Assert.True(FlowNodeKinds.IsTerminal(FlowNodeKinds.Handoff));
        Assert.False(FlowNodeKinds.IsTerminal(FlowNodeKinds.Message));
    }

    private static ListNode List(ListOptions options) =>
        new("n1", "Equipo", new Dictionary<string, string?>(), "Elige", "Ver", options, "equipo", "repeat");

    private static void AssertContains(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{path}: esperaba null y llegó {actual?.ToJsonString()}");
                break;
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual);
                foreach (var (key, value) in expectedObject)
                {
                    Assert.True(actualObject.ContainsKey(key), $"{path}.{key} se perdió al serializar");
                    AssertContains(value, actualObject[key], $"{path}.{key}");
                }

                break;
            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual);
                Assert.Equal(expectedArray.Count, actualArray.Count);
                for (var index = 0; index < expectedArray.Count; index++)
                {
                    AssertContains(expectedArray[index], actualArray[index], $"{path}[{index}]");
                }

                break;
            default:
                Assert.True(
                    JsonNode.DeepEquals(expected, actual),
                    $"{path}: esperaba {expected.ToJsonString()} y llegó {actual?.ToJsonString()}");
                break;
        }
    }
}
