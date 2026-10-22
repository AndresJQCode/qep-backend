using System.Text.Json;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class FlowValidatorTests
{
    private static readonly Guid ActiveApi = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PausedApi = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void AFlowWithoutFirstNodeFails()
    {
        var flow = new FlowDefinition(1, new FlowStart(null), [], [End()]);

        var issue = Single(FlowValidator.Validate(flow, Ctx()), "start.no_first_node");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Null(issue.NodeId);
    }

    [Fact]
    public void AnUnconnectedNextIsAnError()
    {
        var issue = Single(FlowValidator.Validate(Flow(Message("n1", next: null)), Ctx()), "node.output_unconnected");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal("La salida «sigue» de «Saludo» no está conectada.", issue.Message);
    }

    [Fact]
    public void AnUnconnectedInvalidIsAWarning()
    {
        var question = new OpenQuestionNode(
            "n1", "Número de documento", Out(("answered", "end"), ("invalid", null)), "¿Cuál es tu número de documento?",
            "cedula", new QuestionValidation("digits", null, null), 3);

        var issues = FlowValidator.Validate(Flow(question, End()), Ctx());

        var issue = Single(issues, "node.output_unconnected_optional");
        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal("La salida «no válido» de «Número de documento» no está conectada.", issue.Message);
        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
    }

    [Fact]
    public void AnUnknownTargetFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Message("n1", next: "nope")), Ctx()), "node.unknown_target");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
    }

    [Fact]
    public void AnUnreachableNodeWarns()
    {
        var issues = FlowValidator.Validate(Flow(Message("n1"), End(), Message("n9")), Ctx());

        var issue = Single(issues, "node.unreachable");
        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Equal("n9", issue.NodeId);
        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
    }

    // Que nada llegue a un bloque no lo exime: si después se conecta, su salida obligatoria sigue sin destino.
    [Fact]
    public void AnUnreachableNodeWithAnUnconnectedNextStillFails()
    {
        var issues = FlowValidator.Validate(Flow(Message("n1"), End(), Message("n9", next: null)), Ctx());

        Assert.Equal(FlowIssueSeverity.Warning, Single(issues, "node.unreachable").Severity);
        var unconnected = Single(issues, "node.output_unconnected");
        Assert.Equal(FlowIssueSeverity.Error, unconnected.Severity);
        Assert.Equal("n9", unconnected.NodeId);
    }

    [Fact]
    public void ValidatingAHandBuiltFlowWithRepeatedIdsDoesNotThrow()
    {
        var issues = FlowValidator.Validate(Flow(Message("n1"), End(), End()), Ctx());

        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
    }

    [Fact]
    public void FourButtonsFail()
    {
        var buttons = Buttons(new FlowButton("a", "Uno"), new FlowButton("b", "Dos"), new FlowButton("c", "Tres"), new FlowButton("d", "Cuatro"));

        var issue = Single(FlowValidator.Validate(Flow(buttons, End()), Ctx()), "buttons.too_many");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void ALabelOfTwentyOneFails()
    {
        var buttons = Buttons(new FlowButton("a", new string('a', 21)), new FlowButton("b", new string('b', 20)));

        var issue = Single(FlowValidator.Validate(Flow(buttons, End()), Ctx()), "buttons.label_too_long");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
    }

    [Fact]
    public void AnEmptyRepeatedOrReservedButtonIdFails()
    {
        var buttons = Buttons(new FlowButton("", "Uno"), new FlowButton("a", "Dos"), new FlowButton("a", "Tres")) with
        {
            Outputs = Out(("", "end"), ("a", "end"), ("other", "end")),
        };
        var reserved = Buttons(new FlowButton("other", "Otra"));

        var issues = FlowValidator.Validate(Flow(buttons, End()), Ctx());

        Assert.Equal(2, issues.Count(i => i.Code == "buttons.id_invalid" && i.Severity == FlowIssueSeverity.Error && i.NodeId == "n1"));
        Assert.Single(FlowValidator.Validate(Flow(reserved, End()), Ctx()), i => i.Code == "buttons.id_invalid");
    }

    [Fact]
    public void AnEmptyOrRepeatedRowIdFails()
    {
        var rows = new[] { new ListFixedRow("", "Norte", null), new ListFixedRow("s", "Sur", null), new ListFixedRow("s", "Centro", null) };

        var issues = FlowValidator.Validate(Flow(FixedList(rows), End()), Ctx());

        Assert.Equal(2, issues.Count(i => i.Code == "list.row_id_invalid" && i.Severity == FlowIssueSeverity.Error && i.NodeId == "n1"));
    }

    [Fact]
    public void ABlankButtonsSaveToMeansNotSaving()
    {
        var buttons = Buttons(new FlowButton("a", "Uno")) with { SaveTo = " " };

        Assert.Empty(FlowValidator.Validate(Flow(buttons, End()), Ctx()));
    }

    [Fact]
    public void ElevenFixedRowsFail()
    {
        var rows = Enumerable.Range(1, 11).Select(i => new ListFixedRow($"r{i}", $"Fila {i}", null)).ToList();

        var issue = Single(FlowValidator.Validate(Flow(FixedList(rows), End()), Ctx()), "list.too_many_rows");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void ARowTitleOfTwentyFiveFails()
    {
        var rows = new[] { new ListFixedRow("a", new string('a', 25), null), new ListFixedRow("b", new string('b', 24), null) };

        var issue = Single(FlowValidator.Validate(Flow(FixedList(rows), End()), Ctx()), "list.row_title_too_long");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void ARowDescriptionOfSeventyThreeFails()
    {
        var rows = new[] { new ListFixedRow("a", "A", new string('a', 73)), new ListFixedRow("b", "B", new string('b', 72)) };

        var issue = Single(FlowValidator.Validate(Flow(FixedList(rows), End()), Ctx()), "list.row_description_too_long");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void TextOverFourThousandNinetySixFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Message("n1", new string('a', 4097)), End()), Ctx()), "text.too_long");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.DoesNotContain(
            FlowValidator.Validate(Flow(Message("n1", new string('a', 4096)), End()), Ctx()),
            i => i.Code == "text.too_long");
    }

    // D-I1: el cuerpo de un interactivo (botones, lista, botón URL) tiene el tope de Meta, 1024.
    [Fact]
    public void AnInteractiveBodyOverTenTwentyFourFails()
    {
        var buttons = Buttons(new FlowButton("a", "Uno")) with { Text = new string('a', 1025) };

        var issue = Single(FlowValidator.Validate(Flow(buttons, End()), Ctx()), "text.too_long");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void EmptyTextFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Message("n1", "   "), End()), Ctx()), "text.empty");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
    }

    [Fact]
    public void HttpWithoutConnectionFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Http(withConnection: false), End()), Ctx()), "http.connection_missing");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void HttpWithAPausedConnectionFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Http(connectionId: PausedApi), End()), Ctx()), "http.connection_paused");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void HttpWithInvalidJsonBodyFails()
    {
        var http = Http(body: new HttpBody("json", "{ nope", null, null, null));

        var issue = Single(FlowValidator.Validate(Flow(http, End()), Ctx()), "http.body_not_json");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    // Una variable entre comillas o suelta (un número) deja un JSON válido al sustituirla.
    [Fact]
    public void AJsonBodyWithVariablesIsValid()
    {
        var question = new OpenQuestionNode(
            "n1", "Documento", Out(("answered", "n2"), ("invalid", "end")), "¿Documento?", "cedula",
            new QuestionValidation("digits", null, null), null);
        var http = Http(id: "n2", body: new HttpBody("json", "{ \"id\": {cedula}, \"doc\": \"{cedula}\", \"tags\": [{cedula}] }", null, null, null));

        var issues = FlowValidator.Validate(Flow(question, http, End()), Ctx());

        Assert.Empty(issues);
    }

    [Fact]
    public void HttpPathWithSchemeFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Http(path: "https://x"), End()), Ctx()), "http.path_invalid");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void ConditionWithoutRulesFails()
    {
        var condition = new ConditionNode("n1", "¿Cliente?", Out(("yes", "end"), ("no", "end")), "all", []);

        var issue = Single(FlowValidator.Validate(Flow(condition, End()), Ctx()), "condition.no_rules");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void AVariableNeverSetWarns()
    {
        var issues = FlowValidator.Validate(Flow(Message("n1", "Hola {nombre}, tu cliente es {cliente.nombre}"), End()), Ctx());

        // {nombre} es fija (D-M8); {cliente.nombre} no la escribe ningún bloque.
        var issue = Single(issues, "variable.never_set");
        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Contains("{cliente}", issue.Message, StringComparison.Ordinal);
    }

    // D-M8: lo que cuenta es que un bloque la produzca; declararla en variables[] no la llena.
    [Fact]
    public void ADeclaredVariableThatNoBlockWritesStillWarns()
    {
        var flow = Flow(Message("n1", "Tu cliente es {cliente.nombre}"), End()) with { Variables = [new FlowVariable("cliente", "object")] };

        Assert.Equal(FlowIssueSeverity.Warning, Single(FlowValidator.Validate(flow, Ctx()), "variable.never_set").Severity);
    }

    [Fact]
    public void AnInvalidVariableNameFails()
    {
        var question = new OpenQuestionNode(
            "n1", "Documento", Out(("answered", "end"), ("invalid", "end")), "¿Documento?", "1cedula",
            new QuestionValidation("none", null, null), null);

        var issue = Single(FlowValidator.Validate(Flow(question, End()), Ctx()), "variable.name_invalid");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
    }

    [Fact]
    public void ListOnInstagramFails()
    {
        var list = FixedList([new ListFixedRow("a", "A", null)]);

        var issue = Single(FlowValidator.Validate(Flow(list, End()), Ctx(channel: "Instagram")), "list.channel_not_supported");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.DoesNotContain(FlowValidator.Validate(Flow(list, End()), Ctx()), i => i.Code == "list.channel_not_supported");
    }

    [Fact]
    public void NoBusinessHoursWarns()
    {
        var hours = new BusinessHoursNode("n1", "Horario", Out(("open", "end"), ("closed", "end")));

        var issue = Single(FlowValidator.Validate(Flow(hours, End()), Ctx(businessHours: false)), "business_hours.not_assigned");

        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Empty(FlowValidator.Validate(Flow(hours, End()), Ctx()));
    }

    [Fact]
    public void HttpWithoutSampleResponseWarns()
    {
        var issues = FlowValidator.Validate(Flow(Http(withSample: false), End()), Ctx());

        var issue = Single(issues, "http.no_sample_response");
        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Empty(FlowValidator.Validate(Flow(Http(), End()), Ctx()));
    }

    [Fact]
    public void TheAnnexAFlowHasOnlyWarnings()
    {
        var issues = FlowValidator.Validate(AnnexAFlow.Parse(), AnnexAFlow.Context());

        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
        // Lo que el anexo deja abierto a propósito: las salidas opcionales y n12 sin respuesta de ejemplo.
        (string Code, string NodeId)[] expected =
        [
            ("node.output_unconnected_optional", "n2"),
            ("node.output_unconnected_optional", "n5"),
            ("node.output_unconnected_optional", "n6"),
            ("node.output_unconnected_optional", "n7"),
            ("node.output_unconnected_optional", "n10"),
            ("http.no_sample_response", "n12"),
        ];
        Assert.Equal(expected.Order(), issues.Select(i => (i.Code, i.NodeId!)).Order());
    }

    private static FlowIssue Single(IReadOnlyList<FlowIssue> issues, string code) => Assert.Single(issues, i => i.Code == code);

    // Con indexador y no ToDictionary: una prueba de ids repetidos arma salidas con la misma clave.
    private static Dictionary<string, string?> Out(params (string Key, string? Target)[] outputs)
    {
        var dictionary = new Dictionary<string, string?>();
        foreach (var (key, target) in outputs)
        {
            dictionary[key] = target;
        }

        return dictionary;
    }

    private static FlowDefinition Flow(params FlowNode[] nodes) => new(1, new FlowStart(nodes[0].Id), [], nodes);

    private static FlowValidationContext Ctx(string channel = "WhatsApp", bool businessHours = true) =>
        new(new HashSet<string> { channel }, new HashSet<Guid> { ActiveApi }, new HashSet<Guid> { PausedApi }, businessHours);

    private static HandoffNode End() => new("end", "Asesor", Out(), null);

    private static MessageNode Message(string id, string text = "Hola", string? next = "end") =>
        new(id, "Saludo", Out(("next", next)), text, null);

    private static ButtonsNode Buttons(params FlowButton[] buttons) =>
        new("n1", "Motivo", Out([.. buttons.Select(b => (b.Id, (string?)"end")), ("other", "end")]), "¿Qué necesitas?", buttons, null, "repeat");

    private static ListNode FixedList(IReadOnlyList<ListFixedRow> rows) =>
        new(
            "n1", "Equipo", Out(("chosen", "end"), ("other", "end")), "Elige el equipo", "Ver equipos",
            new ListOptions("fixed", rows, null, null, null, null, null), "equipo", "repeat");

    private static HttpNode Http(
        string id = "n1", bool withConnection = true, Guid? connectionId = null, string path = "/customers", HttpBody? body = null,
        bool withSample = true) =>
        new(
            id, "Buscar cliente", Out(("ok", "end"), ("error", "end")),
            withConnection ? connectionId ?? ActiveApi : null,
            "GET", path, [], body, 8, null, [],
            withSample ? JsonDocument.Parse("""{ "data": [] }""").RootElement.Clone() : null);
}
