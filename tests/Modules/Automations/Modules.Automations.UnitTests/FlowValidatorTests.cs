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

    // Lo que el editor ofrece para interpolar (§4.4 «variables conocidas»): fijas, declaradas y escritas, sin repetir ni vacías.
    [Fact]
    public void KnownVariablesAreTheFixedTheDeclaredAndTheWrittenOnes()
    {
        var question = new OpenQuestionNode(
            "n1", "Documento", Out(("answered", "end"), ("invalid", null)), "¿Documento?", "cedula", new QuestionValidation("none", null, null), null);
        var blank = new OpenQuestionNode(
            "n2", "A medias", Out(("answered", "end"), ("invalid", null)), "¿Algo?", "", new QuestionValidation("none", null, null), null);
        var flow = new FlowDefinition(
            1, new FlowStart("n1"), [new FlowVariable("cedula", "text"), new FlowVariable("equipos", "list")], [question, blank, Http(), End()]);

        Assert.Equal(["nombre", "telefono", "empresa", "conexion", "cedula", "equipos"], FlowValidator.KnownVariables(flow));
        Assert.Equal(
            ["nombre", "telefono", "empresa", "conexion", "cedula", "equipos", "cliente", "equipo", "motivo", "factura", "direccion", "orden"],
            FlowValidator.KnownVariables(AnnexAFlow.Parse()));
    }

    [Fact]
    public void AnUnconnectedNextIsAnError()
    {
        var issue = Single(FlowValidator.Validate(Flow(Message("n1", next: null)), Ctx()), "node.output_unconnected");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal("La salida «sigue» de «Saludo» no está conectada.", issue.Message);
    }

    // D-8: `invalid` sin conectar lo cubre el motor (reinicia o repite) y no se avisa.
    [Fact]
    public void AnUnconnectedInvalidIsNotAnIssue()
    {
        var question = new OpenQuestionNode(
            "n1", "Número de documento", Out(("answered", "end"), ("invalid", null)), "¿Cuál es tu número de documento?",
            "cedula", new QuestionValidation("digits", null, null), 3);
        var attachment = new AttachmentNode(
            "n2", "Foto", Out(("received", "end"), ("invalid", null)), "Envíanos una foto", "image", "foto");

        var issues = FlowValidator.Validate(Flow(question, attachment, End()), Ctx());

        Assert.DoesNotContain(issues, i => i.NodeId is "n1" or "n2" && i.Code.StartsWith("node.output_unconnected", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
    }

    [Fact]
    public void AnUnconnectedTooManyIsStillAWarning()
    {
        var list = new ListNode(
            "n1", "Clientes", Out(("chosen", "end"), ("tooMany", null)), "Elige", "Ver",
            new ListOptions("dynamic", null, "clientes", "id", "nombre", null, null), "cliente", "repeat");

        var issue = Single(FlowValidator.Validate(Flow(list, End()), Ctx()), "node.output_unconnected_optional");

        Assert.Equal((FlowIssueSeverity.Warning, "n1"), (issue.Severity, issue.NodeId));
        Assert.Equal("La salida «demasiadas» de «Clientes» no está conectada.", issue.Message);
    }

    // D-3: `other` es obligatoria con otherOutput y no existe con los demás fallbacks.
    [Fact]
    public void AnUnconnectedOtherWithOtherOutputIsAnError()
    {
        var buttons = Buttons(new FlowButton("a", "Sí")) with { Fallback = "otherOutput", Outputs = Out(("a", "end"), ("other", null)) };

        var issues = FlowValidator.Validate(Flow(buttons, End()), Ctx());

        var issue = Single(issues, "node.output_unconnected");
        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
        Assert.Equal("La salida «otra respuesta» de «Motivo» no está conectada.", issue.Message);
    }

    [Fact]
    public void ARepeatFallbackHasNoOtherIssue()
    {
        var buttons = Buttons(new FlowButton("a", "Sí")) with { Outputs = Out(("a", "end"), ("other", null)) };

        var issues = FlowValidator.Validate(Flow(buttons, End()), Ctx());

        Assert.DoesNotContain(issues, i => i.NodeId == "n1");
    }

    [Fact]
    public void AConnectedOtherWithRepeatIsNotFollowedForReachability()
    {
        var buttons = Buttons(new FlowButton("a", "Sí")) with { Outputs = Out(("a", "end"), ("other", "n9")) };

        var issues = FlowValidator.Validate(Flow(buttons, End(), Message("n9")), Ctx());

        var issue = Single(issues, "node.unreachable");
        Assert.Equal((FlowIssueSeverity.Warning, "n9"), (issue.Severity, issue.NodeId));
    }

    // D-5: el destino de un traspaso tiene que poder responder conversaciones.
    private static readonly Guid Agent = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Gone = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void AHandoffToAMemberWhoCannotReplyIsAnError()
    {
        var handoff = new HandoffNode("end", "Asesor", Out(), null, Gone);

        var issue = Single(FlowValidator.Validate(Flow(Message("n1"), handoff), Ctx(members: Agent)), "handoff.member_invalid");

        Assert.Equal((FlowIssueSeverity.Error, "end"), (issue.Severity, issue.NodeId));
        Assert.Equal(
            "«Asesor» asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro o déjala sin asignar.",
            issue.Message);
    }

    [Fact]
    public void AFallbackHandoffToAMemberWhoCannotReplyIsAnError()
    {
        var buttons = Buttons(new FlowButton("a", "Sí")) with { Fallback = "handoff", FallbackMemberId = Gone, Outputs = Out(("a", "end")) };
        var list = FixedList([new ListFixedRow("r1", "Ventas", null)]) with
        {
            Id = "n2", Title = "Equipo", Fallback = "handoff", FallbackMemberId = Gone, Outputs = Out(("chosen", "end")),
        };

        var issues = FlowValidator.Validate(Flow(buttons, list, End()), Ctx(members: Agent));

        var onButtons = Single(issues.Where(i => i.NodeId == "n1").ToList(), "handoff.member_invalid");
        var onList = Single(issues.Where(i => i.NodeId == "n2").ToList(), "handoff.member_invalid");
        Assert.All(new[] { onButtons, onList }, i => Assert.Equal(FlowIssueSeverity.Error, i.Severity));
        Assert.Equal(
            "«Si responde otra cosa» de «Motivo» asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro o déjala sin asignar.",
            onButtons.Message);
        Assert.StartsWith("«Si responde otra cosa» de «Equipo»", onList.Message);
    }

    [Fact]
    public void AnUnassignedHandoffIsValid()
    {
        var issues = FlowValidator.Validate(Flow(Message("n1"), End()), Ctx());

        Assert.DoesNotContain(issues, i => i.Code == "handoff.member_invalid");
    }

    [Fact]
    public void AMemberWhoCanReplyIsValid()
    {
        var handoff = new HandoffNode("end", "Asesor", Out(), null, Agent);
        var buttons = Buttons(new FlowButton("a", "Sí")) with { Fallback = "handoff", FallbackMemberId = Agent, Outputs = Out(("a", "end")) };

        var issues = FlowValidator.Validate(Flow(buttons, handoff), Ctx(members: Agent));

        Assert.DoesNotContain(issues, i => i.Code == "handoff.member_invalid");
    }

    // Un borrador sin normalizar puede traer un miembro huérfano con `repeat`: no cuenta (D-3).
    [Fact]
    public void AFallbackMemberIsIgnoredOutsideHandoff()
    {
        var buttons = Buttons(new FlowButton("a", "Sí")) with { FallbackMemberId = Gone, Outputs = Out(("a", "end")) };

        var issues = FlowValidator.Validate(Flow(buttons, End()), Ctx());

        Assert.DoesNotContain(issues, i => i.Code == "handoff.member_invalid");
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

    /// <summary>Ruling P2-R36: el validador acepta en <c>mapping.from</c> exactamente lo que lee el ejecutor (<c>JsonPath.SelectResponse</c>).</summary>
    [Theory]
    [InlineData("$")]
    [InlineData("[0].id")]
    [InlineData("$[0].id")]
    [InlineData("$.data")]
    [InlineData("data[0].id")]
    public void AResponseMappingPathIsAccepted(string from) =>
        Assert.Empty(FlowValidator.Validate(Flow(Http() with { Mapping = [new HttpMapping(from, "cliente")] }, End()), Ctx()));

    [Theory]
    [InlineData("$$")]
    [InlineData("[x]")]
    [InlineData("$data")]
    [InlineData("data..id")]
    [InlineData("")]
    public void AnUnreadableResponseMappingPathIsAnError(string from)
    {
        var issues = FlowValidator.Validate(Flow(Http() with { Mapping = [new HttpMapping(from, "cliente")] }, End()), Ctx());

        var issue = Single(issues, "http.mapping_path_invalid");
        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
    }

    [Fact]
    public void TheAnnexAFlowHasOnlyWarnings()
    {
        var issues = FlowValidator.Validate(AnnexAFlow.Parse(), AnnexAFlow.Context());

        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
        // Lo que el anexo deja abierto a propósito: n12 sin respuesta de ejemplo. Los `invalid` sin conectar
        // (n2, n7, n10) ya no avisan (D-8) y los Botones/Lista del anexo (n5, n6) usan `repeat`, así que no tienen `other` (D-3).
        (string Code, string NodeId)[] expected =
        [
            ("http.no_sample_response", "n12"),
        ];
        Assert.Equal(expected.Order(), issues.Select(i => (i.Code, i.NodeId!)).Order());
    }

    // Ruling P2-R8: lo que el motor o Meta no pueden ejecutar no se publica.
    [Fact]
    public void AButtonsNodeWithoutButtonsFails()
    {
        var issue = Single(FlowValidator.Validate(Flow(Buttons(), End()), Ctx()), "buttons.empty");

        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
        Assert.Equal("«Motivo» no tiene botones: agrega al menos uno.", issue.Message);
    }

    [Fact]
    public void AFixedListWithoutRowsFailsButAnExtraOptionCountsAsARow()
    {
        var empty = Single(FlowValidator.Validate(Flow(FixedList([]), End()), Ctx()), "list.no_rows");
        Assert.Equal((FlowIssueSeverity.Error, "n1"), (empty.Severity, empty.NodeId));

        var onlyExtra = FixedList([]) with
        {
            Options = new ListOptions("fixed", [], null, null, null, null, new ListFixedRow("otro", "Otro", null)),
            Outputs = Out(("chosen", "end"), ("other", "end"), ("extra", "end")),
        };
        Assert.DoesNotContain(FlowValidator.Validate(Flow(onlyExtra, End()), Ctx()), i => i.Code == "list.no_rows");
    }

    [Theory]
    [InlineData(null, "id", "name")]
    [InlineData("equipos", "", "name")]
    [InlineData("equipos", "id", null)]
    public void ADynamicListNeedsCollectionIdFieldAndTitleField(string? collection, string? idField, string? titleField)
    {
        var list = FixedList([]) with { Options = new ListOptions("dynamic", null, collection, idField, titleField, null, null) };

        var issue = Single(FlowValidator.Validate(Flow(list, End()), Ctx()), "list.dynamic_incomplete");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void ACompleteDynamicListHasNoDynamicIssue()
    {
        var list = FixedList([]) with { Options = new ListOptions("dynamic", null, "nombre", "id", "name", null, null) };

        Assert.DoesNotContain(FlowValidator.Validate(Flow(list, End()), Ctx()), i => i.Code is "list.dynamic_incomplete" or "list.no_rows");
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://x.com/a")]
    [InlineData("/catalogo")]
    [InlineData("x.com")]
    public void AnUrlButtonNeedsAnAbsoluteHttpsUrl(string url)
    {
        var message = new MessageNode("n1", "Saludo", Out(("next", "end")), "Hola", new UrlButton("Ver", url));

        var issue = Single(FlowValidator.Validate(Flow(message, End()), Ctx()), "message.url_invalid");

        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
    }

    [Theory]
    [InlineData("https://qcode.co/catalogo")]
    [InlineData("https://qcode.co/pedido/{nombre}")]
    public void AnHttpsUrlButtonIsValid(string url)
    {
        var message = new MessageNode("n1", "Saludo", Out(("next", "end")), "Hola", new UrlButton("Ver", url));

        Assert.DoesNotContain(FlowValidator.Validate(Flow(message, End()), Ctx()), i => i.Code == "message.url_invalid");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void TheHttpTimeoutMustBeBetween1And10Seconds(int seconds, bool fails)
    {
        var issues = FlowValidator.Validate(Flow(Http() with { TimeoutSeconds = seconds }, End()), Ctx());

        Assert.Equal(fails, issues.Any(i => i.Code == "http.timeout_invalid"));
    }

    [Theory]
    [InlineData("/a/../x")]
    [InlineData("/a/..")]
    [InlineData("/../x")]
    [InlineData("/a/%2E%2e/x")]
    public void AnHttpPathWithADotDotSegmentFails(string path)
    {
        var issue = Single(FlowValidator.Validate(Flow(Http(path: path), End()), Ctx()), "http.path_invalid");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void AnHttpPathWithDotsInANameIsValid() =>
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Http(path: "/v1.2/..a/b.."), End()), Ctx()), i => i.Code == "http.path_invalid");

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(null, false)]
    public void TheQuestionMaxAttemptsMustBeBetween1And5WhenSet(int? attempts, bool fails)
    {
        var issues = FlowValidator.Validate(Flow(Question(new QuestionValidation("none", null, null), attempts), End()), Ctx());

        Assert.Equal(fails, issues.Any(i => i.Code == "question.max_attempts_invalid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("([a-z")]
    public void ARegexValidationNeedsACompilablePattern(string? pattern)
    {
        var issue = Single(FlowValidator.Validate(Flow(Question(new QuestionValidation("regex", pattern, null), 3), End()), Ctx()), "question.pattern_invalid");

        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
    }

    [Fact]
    public void AValidRegexPatternPasses() =>
        Assert.DoesNotContain(
            FlowValidator.Validate(Flow(Question(new QuestionValidation("regex", @"^\d{6,10}$", null), 3), End()), Ctx()),
            i => i.Code == "question.pattern_invalid");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AKeywordsValidationNeedsKeywords(string? onlyWord)
    {
        // null = lista ausente; "" = lista vacía; el resto = una sola palabra en blanco.
        string[]? keywords = onlyWord is null ? null : onlyWord.Length == 0 ? [] : [onlyWord];
        var issue = Single(FlowValidator.Validate(Flow(Question(new QuestionValidation("keywords", null, keywords), 3), End()), Ctx()), "question.keywords_empty");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void AKeywordsValidationWithWordsPasses() =>
        Assert.DoesNotContain(
            FlowValidator.Validate(Flow(Question(new QuestionValidation("keywords", null, ["sí", "no"]), 3), End()), Ctx()),
            i => i.Code == "question.keywords_empty");

    // D-3: Política de datos — texto, aviso sin enlace y las dos salidas fijas.
    [Fact]
    public void AnEmptyConsentTextIsAnError()
    {
        var issue = Single(FlowValidator.Validate(Flow(Consent("  "), End()), Ctx()), "text.empty");

        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
    }

    [Fact]
    public void AConsentTextOver1024IsAnError()
    {
        var issue = Single(FlowValidator.Validate(Flow(Consent(new string('a', 1025)), End()), Ctx()), "text.too_long");

        Assert.Equal((FlowIssueSeverity.Error, "n1"), (issue.Severity, issue.NodeId));
        Assert.Contains("el máximo es 1024", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Acepta nuestra política")]
    [InlineData("Lee empresa.co/datos")]
    public void AConsentTextWithoutALinkIsOnlyAWarning(string text)
    {
        var issues = FlowValidator.Validate(Flow(Consent(text), End()), Ctx());

        var issue = Single(issues, "data_consent.no_link");
        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal("El texto de «Política de datos» no tiene un enlace a tu política de datos.", issue.Message);
        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
    }

    [Theory]
    [InlineData("Lee https://empresa.co/datos")]
    [InlineData("HTTP://empresa.co")]
    public void AConsentTextWithALinkHasNoWarning(string text) =>
        Assert.DoesNotContain(FlowValidator.Validate(Flow(Consent(text), End()), Ctx()), i => i.Code == "data_consent.no_link");

    [Fact]
    public void UnconnectedConsentOutputsAreErrorsNamedLikeTheButtons()
    {
        var accepted = Single(FlowValidator.Validate(Flow(Consent(accepted: null), End()), Ctx()), "node.output_unconnected");
        var rejected = Single(FlowValidator.Validate(Flow(Consent(rejected: null), End()), Ctx()), "node.output_unconnected");

        Assert.Equal(FlowIssueSeverity.Error, accepted.Severity);
        Assert.Equal("La salida «Acepto» de «Política de datos» no está conectada.", accepted.Message);
        Assert.Equal(FlowIssueSeverity.Error, rejected.Severity);
        Assert.Equal("La salida «No acepto» de «Política de datos» no está conectada.", rejected.Message);
    }

    [Fact]
    public void AVariableInTheConsentTextCounts()
    {
        var issue = Single(FlowValidator.Validate(Flow(Consent("{cedula} lee https://empresa.co/datos"), End()), Ctx()), "variable.never_set");

        Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void AConsentReachesBothOutputs()
    {
        var other = new HandoffNode("other", "Otro asesor", Out(), null);
        var flow = Flow(Consent(accepted: "end", rejected: "other"), End(), other);

        Assert.DoesNotContain(FlowValidator.Validate(flow, Ctx()), i => i.Code == "node.unreachable");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void ARepeatCapOutsideOneToFiveIsAnError(int maxAsks)
    {
        var issue = Single(FlowValidator.Validate(Flow(ConsentOn("repeat", maxAsks), End()), Ctx()), "data_consent.max_asks_invalid");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal($"«Preguntar hasta» de «Política de datos» es {maxAsks}: tiene que estar entre 1 y 5.", issue.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ARepeatCapInsideOneToFiveIsFine(int maxAsks) =>
        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn("repeat", maxAsks), End()), Ctx()), i => i.Code == "data_consent.max_asks_invalid");

    [Theory]
    [InlineData("rejectedOutput")]
    [InlineData("handoff")]
    public void TheCapIsOnlyCheckedWhenItRepeats(string onReject) =>
        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn(onReject, 9), End()), Ctx()), i => i.Code == "data_consent.max_asks_invalid");

    [Theory]
    [InlineData("repeat")]
    [InlineData("handoff")]
    public void ARejectMemberWhoCannotReplyIsAnError(string onReject)
    {
        var issue = Single(FlowValidator.Validate(Flow(ConsentOn(onReject, member: Guid.CreateVersion7()), End()), Ctx()), "handoff.member_invalid");

        Assert.Equal(FlowIssueSeverity.Error, issue.Severity);
        Assert.Equal("n1", issue.NodeId);
        Assert.Equal(
            "«Si no acepta» de «Política de datos» asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro o déjala sin asignar.",
            issue.Message);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("handoff")]
    public void ARejectMemberWhoCanReplyOrNoMemberIsFine(string onReject)
    {
        var member = Guid.CreateVersion7();

        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn(onReject, member: member), End()), Ctx(members: member)), i => i.Code == "handoff.member_invalid");
        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn(onReject), End()), Ctx()), i => i.Code == "handoff.member_invalid");
    }

    [Fact]
    public void AnOrphanRejectMemberIsIgnoredWhenItExitsByRejected() =>
        Assert.DoesNotContain(
            FlowValidator.Validate(Flow(ConsentOn("rejectedOutput", member: Guid.CreateVersion7()), End()), Ctx()), i => i.Code == "handoff.member_invalid");

    [Theory]
    [InlineData("repeat")]
    [InlineData("handoff")]
    public void RejectedIsNotRequiredWhenItDoesNotExit(string onReject) =>
        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn(onReject, rejected: null), End()), Ctx()), i => i.Code == "node.output_unconnected");

    [Fact]
    public void RejectedIsRequiredWhenItExits()
    {
        var issue = Single(FlowValidator.Validate(Flow(ConsentOn("rejectedOutput", rejected: null), End()), Ctx()), "node.output_unconnected");

        Assert.Equal("La salida «No acepto» de «Política de datos» no está conectada.", issue.Message);
    }

    // Una `rejected` que quedó en el borrador sin normalizar no abre camino ni se valida cuando el bloque no sale por ahí.
    [Fact]
    public void ABlockBehindAHiddenRejectedIsUnreachable()
    {
        var hidden = new HandoffNode("n9", "Oculto", Out(), null);
        var issues = FlowValidator.Validate(Flow(ConsentOn("repeat", rejected: "n9"), End(), hidden), Ctx());

        Assert.Equal("n9", Single(issues, "node.unreachable").NodeId);
        Assert.DoesNotContain(FlowValidator.Validate(Flow(ConsentOn("repeat", rejected: "nope"), End()), Ctx()), i => i.Code == "node.unknown_target");
    }

    private static DataConsentNode ConsentOn(string onReject, int maxAsks = 3, Guid? member = null, string? rejected = "end") =>
        new("n1", "Política de datos", Out(("accepted", "end"), ("rejected", rejected)), "Lee https://empresa.co/datos", onReject, maxAsks, member);

    private static DataConsentNode Consent(string text = "Lee https://empresa.co/datos", string? accepted = "end", string? rejected = "end") =>
        new("n1", "Política de datos", Out(("accepted", accepted), ("rejected", rejected)), text);

    private static OpenQuestionNode Question(QuestionValidation validation, int? attempts) =>
        new("n1", "Documento", Out(("answered", "end"), ("invalid", "end")), "¿Documento?", "cedula", validation, attempts);

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

    private static FlowValidationContext Ctx(string channel = "WhatsApp", bool businessHours = true, params Guid[] members) =>
        new(new HashSet<string> { channel }, new HashSet<Guid> { ActiveApi }, new HashSet<Guid> { PausedApi }, businessHours, members.ToHashSet());

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
