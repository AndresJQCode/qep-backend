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
        Assert.Equal([new HttpMapping("data[0]", "cliente"), new HttpMapping("data[0].equipments", "equipos")], search.Mapping);
        Assert.NotNull(search.SampleResponse);
        Assert.Equal("n4", search.Outputs["ok"]);
        Assert.Equal("n9", search.Outputs["error"]);

        // n12 viene sin query, timeout ni okWhen: se leen con sus valores por defecto (D-H4).
        var create = Assert.IsType<HttpNode>(flow.Nodes.Single(node => node.Id == "n12"));
        Assert.Empty(create.Query);
        Assert.Equal(8, create.TimeoutSeconds);
        Assert.Null(create.OkWhen);
        Assert.Equal(new HttpMapping("data", "orden"), Assert.Single(create.Mapping));
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
    public void RoundTripsWithoutLoss() => AssertRoundTrips(AnnexAFlow.Json);

    // El anexo no tiene todos los bloques ni todos los campos opcionales; este flujo sí, para que un campo que la escritura
    // olvide no pase escondido.
    [Fact]
    public void RoundTripsEveryKindWithEveryOptionalField()
    {
        Assert.Equal(FlowNodeKinds.All.Count, FlowJson.Parse(EveryKindJson).Nodes.Select(node => node.Kind).Distinct().Count());
        AssertRoundTrips(EveryKindJson);
    }

    [Fact]
    public void AnUnknownKindThrowsFlowFormat()
    {
        const string json = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [ { "kind": "teleport" } ] }
            """;

        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(json));

        Assert.Equal("flow.unknown_kind", exception.Code);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "resolve", "title": "A" }, { "id": "n1", "kind": "resolve", "title": "B" } ] }""")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "http", "title": "A", "connectionId": "…", "method": "GET", "path": "/x" } ] }""")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "http", "title": "A", "method": "DELETE", "path": "/x" } ] }""")]
    [InlineData("""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "buttons", "title": "A", "text": "B", "buttons": [], "fallback": "nope" } ] }""")]
    public void AMalformedFlowThrowsFlowFormat(string json)
    {
        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(json));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Fact]
    public void ASchemaOtherThanOneIsMalformed()
    {
        var exception = Assert.Throws<FlowFormatException>(
            () => FlowJson.Parse("""{ "schema": 2, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }"""));

        Assert.Equal("flow.malformed", exception.Code);
    }

    // D-C3: el borrador a medio hacer se guarda. Lo que el admin no ha escrito llega vacío y lo dice el validador.
    [Fact]
    public void ABlankDraftFieldParsesAsEmptyAndTheValidatorReportsIt()
    {
        const string json = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "message", "outputs": { "next": "n2" } },
                { "id": "n2", "kind": "condition", "title": null, "match": "all", "rules": [ { "operator": "isEmpty" } ],
                  "outputs": { "yes": "n3", "no": "n3" } },
                { "id": "n3", "kind": "resolve" } ] }
            """;

        var flow = FlowJson.Parse(json);

        var message = Assert.IsType<MessageNode>(flow.Nodes[0]);
        Assert.Equal("", message.Title);
        Assert.Equal("", message.Text);
        Assert.Equal("", Assert.Single(Assert.IsType<ConditionNode>(flow.Nodes[1]).Rules).Variable);
        var issues = FlowValidator.Validate(flow, AnnexAFlow.Context());
        Assert.Equal(
            [("text.empty", "n1"), ("variable.name_invalid", "n2")],
            issues.Select(issue => (issue.Code, issue.NodeId!)));
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

    private const string EveryKindJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "m1" },
          "variables": [ { "name": "doc", "type": "text" }, { "name": "foto", "type": "attachment" } ],
          "nodes": [
            { "id": "m1", "kind": "message", "title": "Hola", "text": "Hola {nombre}", "urlButton": { "label": "Ver", "url": "https://www.example.com" }, "outputs": { "next": "q1" } },
            { "id": "q1", "kind": "openQuestion", "title": "Documento", "text": "¿Cuál es tu documento?", "saveTo": "doc", "validation": { "kind": "regex", "pattern": "^[0-9]{6,10}$", "keywords": null }, "maxAttempts": 2, "outputs": { "answered": "q2", "invalid": "h1" } },
            { "id": "q2", "kind": "openQuestion", "title": "Política", "text": "Escribe acepto", "saveTo": "acepta", "validation": { "kind": "keywords", "pattern": null, "keywords": [ "acepto", "sí" ] }, "maxAttempts": null, "outputs": { "answered": "b1", "invalid": "h1" } },
            { "id": "b1", "kind": "buttons", "title": "Motivo", "text": "¿Qué necesitas?", "buttons": [ { "id": "a", "label": "Uno" }, { "id": "b", "label": "Dos" } ], "saveTo": "motivo", "fallback": "otherOutput", "outputs": { "a": "l1", "b": "l1", "other": "h1" } },
            { "id": "l1", "kind": "list", "title": "Sede", "text": "Elige la sede", "buttonLabel": "Ver sedes", "options": { "kind": "fixed", "fixed": [ { "id": "n", "title": "Norte", "description": "Calle 100" }, { "id": "s", "title": "Sur", "description": null } ], "collection": null, "idField": null, "titleField": null, "descriptionField": null, "extraOption": { "id": "x", "title": "Otra", "description": "Escríbela" } }, "saveTo": "sede", "fallback": "repeat", "outputs": { "chosen": "a1", "extra": "a1", "other": "h1" } },
            { "id": "a1", "kind": "attachment", "title": "Foto", "text": "Envía la foto", "accept": "image", "saveTo": "foto", "outputs": { "received": "c1", "invalid": "h1" } },
            { "id": "c1", "kind": "condition", "title": "¿Hay documento?", "match": "any", "rules": [ { "variable": "doc", "operator": "equals", "value": "123" }, { "variable": "foto.url", "operator": "isNotEmpty", "value": null } ], "outputs": { "yes": "bh", "no": "h1" } },
            { "id": "bh", "kind": "businessHours", "title": "Horario", "outputs": { "open": "w1", "closed": "h1" } },
            { "id": "w1", "kind": "http", "title": "Crear cliente", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "POST", "path": "/customers/{doc}", "query": [ { "key": "source", "value": "bot" } ], "body": { "kind": "multipart", "template": null, "fields": [ { "key": "doc", "value": "{doc}" } ], "fileField": "file", "fileVariable": "foto" }, "timeoutSeconds": 5, "okWhen": [ 200, 201 ], "mapping": [ { "from": "data.id", "to": "orden" } ], "sampleResponse": { "data": { "id": 7, "tags": [ "a" ] } }, "outputs": { "ok": "r1", "error": "h1" } },
            { "id": "h1", "kind": "handoff", "title": "Asesor", "text": "Te paso con un asesor.", "outputs": {} },
            { "id": "r1", "kind": "resolve", "title": "Listo", "text": "Tu orden es la {orden.id}.", "reasonId": "33333333-3333-3333-3333-333333333333", "outputs": {} }
          ]
        }
        """;

    private static void AssertRoundTrips(string json)
    {
        var first = FlowJson.Serialize(FlowJson.Parse(json));
        var second = FlowJson.Serialize(FlowJson.Parse(first));

        Assert.Equal(first, second);
        AssertContains(JsonNode.Parse(json), JsonNode.Parse(first), "$");
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
