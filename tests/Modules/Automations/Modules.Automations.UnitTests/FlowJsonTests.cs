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

    // Spec 2026-10-25 D-3: «otra respuesta» es una salida sólo cuando el canvas la dibuja, o sea con otherOutput.
    [Theory]
    [InlineData("repeat", false)]
    [InlineData("handoff", false)]
    [InlineData("otherOutput", true)]
    public void OutputsOfButtonsAndListIncludeOtherOnlyWithOtherOutput(string fallback, bool includesOther)
    {
        var buttons = new ButtonsNode(
            "n1", "Motivo", new Dictionary<string, string?>(), "¿Qué necesitas?",
            [new FlowButton("tech", "Servicio técnico"), new FlowButton("maint", "Mantenimiento")], null, fallback);
        var list = List(new ListOptions("fixed", [new ListFixedRow("a", "A", null)], null, null, null, null, null)) with { Fallback = fallback };

        Assert.Equal(includesOther ? ["tech", "maint", "other"] : ["tech", "maint"], FlowNodeKinds.OutputsOf(buttons));
        Assert.Equal(includesOther ? ["chosen", "other"] : ["chosen"], FlowNodeKinds.OutputsOf(list));
    }

    private const string DestinationsJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "b1" },
          "variables": [],
          "nodes": [
            { "id": "b1", "kind": "buttons", "title": "B", "text": "t", "buttons": [ { "id": "a", "label": "A" } ], "saveTo": null, "fallback": "handoff", "fallbackMemberId": "44444444-4444-4444-4444-444444444444", "outputs": { "a": "l1" } },
            { "id": "l1", "kind": "list", "title": "L", "text": "t", "buttonLabel": "Ver", "options": { "kind": "fixed", "fixed": [ { "id": "n", "title": "N", "description": null } ], "collection": null, "idField": null, "titleField": null, "descriptionField": null, "extraOption": null }, "saveTo": "x", "fallback": "handoff", "fallbackMemberId": "55555555-5555-5555-5555-555555555555", "outputs": { "chosen": "h1" } },
            { "id": "h1", "kind": "handoff", "title": "H", "text": null, "memberId": "66666666-6666-6666-6666-666666666666", "outputs": {} }
          ]
        }
        """;

    [Fact]
    public void TheHandoffDestinationsRoundTrip()
    {
        var flow = FlowJson.Parse(DestinationsJson);

        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), Assert.IsType<ButtonsNode>(flow.Nodes[0]).FallbackMemberId);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), Assert.IsType<ListNode>(flow.Nodes[1]).FallbackMemberId);
        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), Assert.IsType<HandoffNode>(flow.Nodes[2]).MemberId);
        // Los records con listas no se comparan por valor: el idempotente de Serialize es la prueba de ida y vuelta.
        var serialized = FlowJson.Serialize(flow);
        Assert.Equal(serialized, FlowJson.Serialize(FlowJson.Parse(serialized)));
        AssertContains(JsonNode.Parse(DestinationsJson), JsonNode.Parse(serialized), "$");
    }

    [Fact]
    public void SerializeWritesNullDestinationsWhenThereAreNone()
    {
        var json = FlowJson.Serialize(FlowJson.Parse(EveryKindJson));
        var nodes = JsonNode.Parse(json)!["nodes"]!.AsArray();

        foreach (var kind in new[] { "buttons", "list", "handoff" })
        {
            var node = nodes.Single(n => n!["kind"]!.GetValue<string>() == kind)!.AsObject();
            var key = kind == "handoff" ? "memberId" : "fallbackMemberId";
            Assert.True(node.ContainsKey(key), $"{kind} no escribió {key}");
            Assert.Null(node[key]);
        }
    }

    // D-4: el JSON de antes de este cambio no trae las claves y se lee «sin asignar».
    [Fact]
    public void AFlowWithoutDestinationsReadsUnassigned()
    {
        var flow = FlowJson.Parse(EveryKindJson);

        Assert.All(flow.Nodes.OfType<ButtonsNode>(), node => Assert.Null(node.FallbackMemberId));
        Assert.All(flow.Nodes.OfType<ListNode>(), node => Assert.Null(node.FallbackMemberId));
        Assert.All(flow.Nodes.OfType<HandoffNode>(), node => Assert.Null(node.MemberId));
    }

    [Theory]
    [InlineData("""{ "id": "h1", "kind": "handoff", "title": "H", "memberId": "ana" }""")]
    [InlineData("""{ "id": "b1", "kind": "buttons", "title": "B", "text": "t", "buttons": [], "fallback": "handoff", "fallbackMemberId": "ana" }""")]
    public void ADestinationThatIsNotAGuidIsMalformed(string node)
    {
        var json = $$"""{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [ {{node}} ] }""";

        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(json));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Fact]
    public void OutputsOfAListDependOnItsOptions()
    {
        var fixedRows = new ListOptions("fixed", [new ListFixedRow("a", "A", null)], null, null, null, null, null);
        var dynamicWithExtra = new ListOptions("dynamic", null, "equipos", "id", "name", null, new ListFixedRow("x", "Otra", null));

        Assert.Equal(["chosen"], FlowNodeKinds.OutputsOf(List(fixedRows)));
        Assert.Equal(["chosen", "extra", "tooMany"], FlowNodeKinds.OutputsOf(List(dynamicWithExtra)));
        Assert.Equal(["chosen", "other"], FlowNodeKinds.OutputsOf(List(fixedRows) with { Fallback = "otherOutput" }));
        Assert.Empty(FlowNodeKinds.OutputsOf(new ResolveNode("n1", "Listo", new Dictionary<string, string?>(), null, null)));
        Assert.True(FlowNodeKinds.IsTerminal(FlowNodeKinds.Handoff));
        Assert.False(FlowNodeKinds.IsTerminal(FlowNodeKinds.Message));
    }

    private const string DataConsentJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n7" },
          "variables": [],
          "nodes": [
            { "id": "n7", "kind": "dataConsent", "title": "Política de datos", "text": "Hola {nombre}, lee https://empresa.co/datos y dinos si aceptas.", "outputs": { "accepted": "n8", "rejected": "n9" } },
            { "id": "n8", "kind": "resolve", "title": "Fin", "text": null, "reasonId": null },
            { "id": "n9", "kind": "resolve", "title": "Fin", "text": null, "reasonId": null }
          ]
        }
        """;

    [Fact]
    public void ADataConsentNodeRoundTrips()
    {
        var flow = FlowJson.Parse(DataConsentJson);
        var consent = Assert.IsType<DataConsentNode>(flow.Nodes[0]);

        Assert.Equal("Hola {nombre}, lee https://empresa.co/datos y dinos si aceptas.", consent.Text);
        Assert.Equal("Política de datos", consent.Title);
        Assert.Equal("n8", consent.Outputs["accepted"]);
        Assert.Equal("n9", consent.Outputs["rejected"]);

        var serialized = FlowJson.Serialize(flow);
        var again = Assert.IsType<DataConsentNode>(FlowJson.Parse(serialized).Nodes[0]);
        Assert.Equal(consent.Text, again.Text);
        Assert.Equal(consent.Outputs, again.Outputs);
        Assert.Equal(serialized, FlowJson.Serialize(FlowJson.Parse(serialized)));

        var written = Assert.IsType<JsonObject>(JsonNode.Parse(serialized)!["nodes"]![0]);
        Assert.False(written.ContainsKey("buttons"));
        Assert.False(written.ContainsKey("saveTo"));
        Assert.False(written.ContainsKey("fallback"));
    }

    [Fact]
    public void ADataConsentWithoutTextReadsEmpty()
    {
        var flow = FlowJson.Parse(DataConsentJson.Replace("\"text\": \"Hola {nombre}, lee https://empresa.co/datos y dinos si aceptas.\", ", string.Empty));

        Assert.Equal(string.Empty, Assert.IsType<DataConsentNode>(flow.Nodes[0]).Text);
    }

    // Spec 2026-10-26 repetir R-1, R-2: el modo, el tope y el destino de «Si no acepta» viajan en el bloque.
    private const string DataConsentRepeatJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n7" },
          "variables": [],
          "nodes": [
            { "id": "n7", "kind": "dataConsent", "title": "Política de datos", "text": "Lee https://empresa.co/datos y dinos si aceptas.",
              "onReject": "repeat", "maxAsks": 3, "onRejectMemberId": null, "outputs": { "accepted": "n8" } },
            { "id": "n8", "kind": "resolve", "title": "Fin", "text": null, "reasonId": null }
          ]
        }
        """;

    [Fact]
    public void ADataConsentWithItsRejectSettingsRoundTrips()
    {
        var flow = FlowJson.Parse(DataConsentRepeatJson);
        var consent = Assert.IsType<DataConsentNode>(flow.Nodes[0]);

        Assert.Equal(("repeat", 3, (Guid?)null), (consent.OnReject, consent.MaxAsks, consent.OnRejectMemberId));

        var serialized = FlowJson.Serialize(flow);
        var again = Assert.IsType<DataConsentNode>(FlowJson.Parse(serialized).Nodes[0]);
        Assert.Equal((consent.OnReject, consent.MaxAsks, consent.OnRejectMemberId), (again.OnReject, again.MaxAsks, again.OnRejectMemberId));
        Assert.Equal(consent.Outputs, again.Outputs);
        var written = Assert.IsType<JsonObject>(JsonNode.Parse(serialized)!["nodes"]![0]);
        Assert.Equal("repeat", written["onReject"]!.GetValue<string>());
        Assert.Equal(3, written["maxAsks"]!.GetValue<int>());
        Assert.True(written.ContainsKey("onRejectMemberId"));
        Assert.Null(written["onRejectMemberId"]);
    }

    // Review Focus 2: un bloque guardado antes de R-1 se lee como hoy, saliendo por «No acepto».
    [Fact]
    public void AnOldDataConsentReadsAsRejectedOutput()
    {
        var consent = Assert.IsType<DataConsentNode>(FlowJson.Parse(DataConsentJson).Nodes[0]);

        Assert.Equal(("rejectedOutput", 3, (Guid?)null), (consent.OnReject, consent.MaxAsks, consent.OnRejectMemberId));
        Assert.Equal("n9", consent.Outputs["rejected"]);
    }

    [Fact]
    public void AnUnknownRejectActionIsMalformed()
    {
        var exception = Assert.Throws<FlowFormatException>(() => FlowJson.Parse(DataConsentRepeatJson.Replace("\"onReject\": \"repeat\"", "\"onReject\": \"ask\"")));

        Assert.Equal("flow.malformed", exception.Code);
    }

    [Theory]
    [InlineData("rejectedOutput", new[] { "accepted", "rejected" })]
    [InlineData("repeat", new[] { "accepted" })]
    [InlineData("handoff", new[] { "accepted" })]
    public void OutputsOfADataConsentDependOnTheRejectAction(string onReject, string[] expected)
    {
        var node = new DataConsentNode("n7", "Política", new Dictionary<string, string?>(), "t", onReject);

        Assert.Equal(expected, FlowNodeKinds.OutputsOf(node));
    }

    [Fact]
    public void OutputsOfADataConsentAreAcceptedAndRejected()
    {
        var node = new DataConsentNode("n7", "Política", new Dictionary<string, string?>(), "t");

        Assert.Equal(["accepted", "rejected"], FlowNodeKinds.OutputsOf(node));
        Assert.Contains(FlowNodeKinds.DataConsent, FlowNodeKinds.All);
        Assert.Equal(["Acepto", "No acepto"], DataConsentButtons.All.Select(button => button.Label));
        Assert.Equal(["accepted", "rejected"], DataConsentButtons.All.Select(button => button.Id));
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
            { "id": "dc", "kind": "dataConsent", "title": "Datos", "text": "Lee https://empresa.co/datos y acepta.", "outputs": { "accepted": "r1", "rejected": "h1" } },
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
