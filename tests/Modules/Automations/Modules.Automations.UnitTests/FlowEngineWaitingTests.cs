using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class FlowEngineWaitingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private static readonly Guid InvoiceMessageId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static FlowDefinition Flow(params FlowNode[] nodes) => new(FlowDefinition.CurrentSchema, new FlowStart(nodes[0].Id), [], nodes);

    private static EngineSettings Settings() => new("reiniciar", 3);

    private static StepResult Start(FlowDefinition flow, RunVariables? variables = null) =>
        FlowEngine.Step(flow, Settings(), RunState.Initial(variables ?? RunVariables.Empty()), new EngineInput.Start(), Now);

    private static StepResult Reply(FlowDefinition flow, StepResult previous, EngineInput.CustomerMessage message) =>
        FlowEngine.Step(flow, Settings(), previous.State, message, Now);

    /// <summary>Varias respuestas seguidas, una por paso, como las procesa el worker (§5.2: un entrante por unidad).</summary>
    private static StepResult Replies(FlowDefinition flow, StepResult previous, params EngineInput.CustomerMessage[] messages) =>
        messages.Aggregate(previous, (result, message) => Reply(flow, result, message));

    private static EngineInput.CustomerMessage Typed(string text) => new("Text", text, null, null);

    private static EngineInput.CustomerMessage Tapped(string id, string title) => new("Interactive", title, id, null);

    private static EngineInput.CustomerMessage Attachment(string kind, string? fileName, string mimeType) =>
        new(kind, null, null, new AttachmentInput(InvoiceMessageId, fileName, mimeType, Stored: false));

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);

    private static MessageNode Message(string id, string text, string? next = null) => new(id, $"Mensaje {id}", Out((FlowOutputs.Next, next)), text, null);

    private static OpenQuestionNode Question(
        string id, string validation = QuestionValidationKinds.None, string? answered = "ok", string? invalid = null, string saveTo = "cedula",
        IReadOnlyList<string>? keywords = null, int? maxAttempts = null, string? pattern = null) =>
        new(id, "Número de documento", Out((FlowOutputs.Answered, answered), (FlowOutputs.Invalid, invalid)), "¿Cuál es tu número de documento?",
            saveTo, new QuestionValidation(validation, pattern, keywords), maxAttempts);

    private static ButtonsNode Buttons(string id, string fallback = InteractiveFallbacks.Repeat, string? other = null) =>
        new(id, "Motivo", Out(("tech", "t"), ("sales", "s"), (FlowOutputs.Other, other)), "¿En qué te ayudamos?",
            [new FlowButton("tech", "Servicio técnico"), new FlowButton("sales", "Ventas")], "motivo", fallback);

    private static ListNode EquipmentList(string id, bool extra = true, string fallback = InteractiveFallbacks.Repeat, string? tooMany = "muchos") =>
        new(id, "Equipo", Out((FlowOutputs.Chosen, "elegido"), (FlowOutputs.Extra, "otra"), (FlowOutputs.TooMany, tooMany), (FlowOutputs.Other, null)),
            "¿Cuál equipo?", "Ver equipos",
            new ListOptions(ListOptionKinds.Dynamic, null, "equipos", "id", "referenceCode", "address",
                extra ? new ListFixedRow("otra", "Otra dirección", null) : null),
            "equipo", fallback);

    private static AttachmentNode Invoice(string id, string accept) =>
        new(id, "Factura", Out((FlowOutputs.Received, "ok"), (FlowOutputs.Invalid, null)), "Envíanos la factura.", accept, "factura");

    private static RunVariables Variables(string json) => RunVariables.Parse(json);

    /// <summary>La lista de equipos con los elementos dados tal cual (para filas raras: sin id, repetidas, largas).</summary>
    private static RunVariables Equipment(params JsonObject[] items) =>
        Variables(new JsonObject { ["equipos"] = new JsonArray([.. items]) }.ToJsonString());

    private static IReadOnlyList<(string Id, string Title, string? Description)> SentRows(StepResult result) =>
        Assert.IsType<InteractiveMessage.List>(Assert.IsType<Effect.SendInteractive>(result.Effects[0]).Message).Rows;

    private static FlowDefinition EquipmentFlow(ListNode list) =>
        Flow(list, Message("elegido", "Listo"), Message("otra", "¿Cuál dirección?"), Message("muchos", "Son muchos"));

    private static RunVariables Equipment(int count) => Variables(new JsonObject
    {
        ["equipos"] = new JsonArray([.. Enumerable.Range(1, count).Select(index => (JsonNode)new JsonObject
        {
            ["id"] = index,
            ["referenceCode"] = $"CAL-{index}",
            ["address"] = $"Cra {index + 4}",
        })]),
    }.ToJsonString());

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"Esperaba {expected} y llegó {actual?.ToJsonString()}.");

    private static void AssertResent(StepResult result, string nodeId, int attempts)
    {
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal(nodeId, result.State.CurrentNodeId);
        Assert.Equal(attempts, result.State.Attempts);
        Assert.Equal(2, result.Effects.Count);
        Assert.Equal(nodeId, result.Effects[0].NodeId);
        Assert.IsType<Effect.WaitForCustomer>(result.Effects[1]);
    }

    [Fact]
    public void ADigitsAnswerIsSavedAndRoutesAnswered()
    {
        var flow = Flow(Question("n1", "digits"), Message("ok", "Gracias, {cedula}"));

        var result = Reply(flow, Start(flow), Typed("  10203 "));

        Assert.Equal("10203", result.State.Variables.Get("cedula")!.GetValue<string>());
        Assert.Equal<Effect>([new Effect.SendText("ok", "Gracias, 10203"), new Effect.EndRun("ok", "output_unconnected")], result.Effects);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "n1", Outcome: FlowOutputs.Answered });

        // La corrida ya terminó: otro mensaje no la mueve.
        var after = Reply(flow, result, Typed("hola"));
        Assert.Same(result.State, after.State);
        Assert.Empty(after.Effects);
    }

    [Fact]
    public void ANumberAnswerIsSavedAsNumber()
    {
        var flow = Flow(Question("n1", "number", saveTo: "monto"), Message("ok", "Listo"));

        var result = Reply(flow, Start(flow), Typed("1234,5"));

        var saved = Assert.IsAssignableFrom<JsonValue>(result.State.Variables.Get("monto"));
        Assert.Equal(JsonValueKind.Number, saved.GetValueKind());
        Assert.Equal(1234.5m, saved.GetValue<decimal>());
        Assert.Equal("ok", result.State.CurrentNodeId);
    }

    [Fact]
    public void AKeywordsAnswerMatchesWithoutAccents()
    {
        var flow = Flow(Question("n1", QuestionValidationKinds.Keywords, saveTo: "politica", keywords: ["acepto"]), Message("ok", "Gracias"));

        var result = Reply(flow, Start(flow), Typed("ACEPTÓ"));

        Assert.Equal("ok", result.State.CurrentNodeId);
        Assert.Equal("ACEPTÓ", result.State.Variables.Get("politica")!.GetValue<string>());
    }

    [Fact]
    public void AnAudioToAnOpenQuestionIsInvalidAndRepeats()
    {
        var flow = Flow(Question("n1"), Message("ok", "Gracias"));
        var started = Start(flow);

        var result = Reply(flow, started, Attachment("Audio", null, "audio/ogg"));

        Assert.Equal<Effect>(
            [new Effect.SendText("n1", "¿Cuál es tu número de documento?"), new Effect.WaitForCustomer("n1", "Número de documento")],
            result.Effects);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal(1, result.State.Attempts);
        // Ruling P2-R5: reenviar es volver a entrar, así que el reenvío lleva su propio StepNumber (otro clientId).
        Assert.Equal(started.State.StepNumber + 1, result.State.StepNumber);
        Assert.False(result.State.Variables.Has("cedula"));
    }

    [Fact]
    public void ThreeInvalidAnswersTakeTheInvalidOutputWhenConnected()
    {
        var flow = Flow(Question("n1", "digits", invalid: "bad"), Message("ok", "Gracias"), Message("bad", "No pudimos validar tu documento."));
        var started = Start(flow);

        var first = Reply(flow, started, Typed("abc"));
        var second = Reply(flow, first, Typed("12a"));
        var third = Reply(flow, second, Typed("doce"));

        AssertResent(first, "n1", attempts: 1);
        AssertResent(second, "n1", attempts: 2);
        Assert.Equal<Effect>(
            [new Effect.SendText("bad", "No pudimos validar tu documento."), new Effect.EndRun("bad", "output_unconnected")], third.Effects);
        Assert.Equal(0, third.State.Attempts);
        Assert.Contains(third.State.Trace, entry => entry is { NodeId: "n1", Outcome: FlowOutputs.Invalid });
    }

    [Fact]
    public void ThreeInvalidAnswersRestartWhenNotConnected()
    {
        var fixedVariables = Variables("""{ "nombre": "Ana", "telefono": "573001112233", "empresa": "Clasic", "conexion": "Línea de servicio" }""");
        var flow = Flow(Question("q0", saveTo: "motivo", answered: "q1"), Question("q1", "digits", invalid: null));
        var waiting = Reply(flow, Start(flow, fixedVariables), Typed("Tengo una falla"));

        Assert.Equal("q1", waiting.State.CurrentNodeId);
        Assert.True(waiting.State.Variables.Has("motivo"));

        var result = Replies(flow, waiting, Typed("abc"), Typed("abc"), Typed("abc"));

        // D-M7: vuelve al primer bloque y manda su mensaje, como un Start.
        Assert.Equal<Effect>(
            [new Effect.SendText("q0", "¿Cuál es tu número de documento?"), new Effect.WaitForCustomer("q0", "Número de documento")],
            result.Effects);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal("q0", result.State.CurrentNodeId);
        Assert.Equal(0, result.State.Attempts);
        Assert.Equal(["conexion", "empresa", "nombre", "telefono"], result.State.Variables.Snapshot().Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Ana", result.State.Variables.Get("nombre")!.GetValue<string>());
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "q1", Outcome: "restart" });
    }

    [Fact]
    public void AButtonReplyIdRoutesItsOutput()
    {
        var flow = Flow(Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));

        var result = Reply(flow, Start(flow), Tapped("tech", "Servicio técnico"));

        Assert.Equal("t", result.State.CurrentNodeId);
        AssertJson("""{ "id": "tech", "title": "Servicio técnico" }""", result.State.Variables.Get("motivo"));
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "b", Outcome: "tech" });
    }

    [Fact]
    public void ATypedTitleMatchesWithoutAccentsOrCase()
    {
        var flow = Flow(Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));

        var result = Reply(flow, Start(flow), Typed("  SERVICIO   tecnico "));

        Assert.Equal("t", result.State.CurrentNodeId);
        AssertJson("""{ "id": "tech", "title": "Servicio técnico" }""", result.State.Variables.Get("motivo"));
    }

    [Fact]
    public void AnUnknownButtonReplyIsInvalid()
    {
        var flow = Flow(Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));
        var started = Start(flow);

        var typed = Reply(flow, started, Typed("quiero hablar con alguien"));

        AssertResent(typed, "b", attempts: 1);
        var resent = Assert.IsType<Effect.SendInteractive>(typed.Effects[0]);
        var buttons = Assert.IsType<InteractiveMessage.Buttons>(resent.Message);
        Assert.Equal([("tech", "Servicio técnico"), ("sales", "Ventas")], buttons.Items);
        Assert.False(typed.State.Variables.Has("motivo"));

        // Un id que no es de este bloque (un botón de un mensaje viejo) tampoco vale, aunque el título coincida.
        AssertResent(Reply(flow, typed, Tapped("borrado", "Ventas")), "b", attempts: 2);
    }

    [Fact]
    public void ADynamicListBuildsRowsFromTheCollection()
    {
        var flow = Flow(EquipmentList("l"), Message("elegido", "Listo"), Message("otra", "¿Cuál dirección?"), Message("muchos", "Son muchos"));

        var result = Start(flow, Equipment(1));

        var send = Assert.IsType<Effect.SendInteractive>(result.Effects[0]);
        var list = Assert.IsType<InteractiveMessage.List>(send.Message);
        Assert.Equal("¿Cuál equipo?", list.Body);
        Assert.Equal("Ver equipos", list.ButtonLabel);
        Assert.Equal([("1", "CAL-1", (string?)"Cra 5"), ("otra", "Otra dirección", null)], list.Rows);
        Assert.Equal(new Effect.WaitForCustomer("l", "Equipo"), result.Effects[1]);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
    }

    [Fact]
    public void ADynamicListWithElevenItemsTakesTooManyWithoutSending()
    {
        var flow = Flow(EquipmentList("l", extra: false), Message("elegido", "Listo"), Message("otra", "¿Cuál dirección?"), Message("muchos", "Son muchos"));

        var result = Start(flow, Equipment(11));

        Assert.DoesNotContain(result.Effects, effect => effect is Effect.SendInteractive);
        Assert.Equal<Effect>([new Effect.SendText("muchos", "Son muchos"), new Effect.EndRun("muchos", "output_unconnected")], result.Effects);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "l", Outcome: FlowOutputs.TooMany });

        // El tope es el de Meta: diez filas en total, contando la extraOption.
        var tenAndExtra = Start(Flow(EquipmentList("l"), Message("elegido", "Listo"), Message("otra", "Otra"), Message("muchos", "Son muchos")), Equipment(10));
        Assert.Equal("muchos", tenAndExtra.State.CurrentNodeId);
    }

    [Fact]
    public void AnEmptyCollectionWithoutExtraTakesTooMany()
    {
        var flow = Flow(EquipmentList("l", extra: false), Message("elegido", "Listo"), Message("otra", "¿Cuál dirección?"), Message("muchos", "Ninguno"));

        var empty = Start(flow, Equipment(0));
        var absent = Start(flow);

        Assert.Equal<Effect>([new Effect.SendText("muchos", "Ninguno"), new Effect.EndRun("muchos", "output_unconnected")], empty.Effects);
        Assert.Equal("muchos", absent.State.CurrentNodeId);
    }

    [Fact]
    public void AListChoiceSavesTheItem()
    {
        var flow = Flow(EquipmentList("l"), Message("elegido", "Equipo {equipo.title}"), Message("otra", "Otra"), Message("muchos", "Son muchos"));
        var started = Start(flow, Equipment(2));

        var tapped = Reply(flow, started, Tapped("1", "CAL-1"));

        Assert.Equal(new Effect.SendText("elegido", "Equipo CAL-1"), tapped.Effects[0]);
        AssertJson(
            """{ "id": "1", "title": "CAL-1", "item": { "id": 1, "referenceCode": "CAL-1", "address": "Cra 5" } }""",
            tapped.State.Variables.Get("equipo"));
        Assert.Contains(tapped.State.Trace, entry => entry is { NodeId: "l", Outcome: FlowOutputs.Chosen });

        // Escrito sin tocar la fila: se compara con los títulos.
        var typed = Reply(flow, started, Typed("cal-2"));
        AssertJson(
            """{ "id": "2", "title": "CAL-2", "item": { "id": 2, "referenceCode": "CAL-2", "address": "Cra 6" } }""",
            typed.State.Variables.Get("equipo"));
    }

    [Fact]
    public void TheExtraRowRoutesExtra()
    {
        var flow = Flow(EquipmentList("l"), Message("elegido", "Listo"), Message("otra", "¿Cuál dirección?"), Message("muchos", "Son muchos"));
        var started = Start(flow, Equipment(1));

        var tapped = Reply(flow, started, Tapped("otra", "Otra dirección"));
        var typed = Reply(flow, started, Typed("otra direccion"));

        Assert.Equal("otra", tapped.State.CurrentNodeId);
        Assert.Equal("otra", typed.State.CurrentNodeId);
        Assert.Contains(tapped.State.Trace, entry => entry is { NodeId: "l", Outcome: FlowOutputs.Extra });
        Assert.False(tapped.State.Variables.Has("equipo"));
    }

    [Fact]
    public void AnImageToAnAttachmentAcceptingDocumentsIsInvalid()
    {
        var flow = Flow(Invoice("n1", "document"), Message("ok", "Recibida"));

        var result = Reply(flow, Start(flow), Attachment("Image", null, "image/jpeg"));

        Assert.Equal<Effect>([new Effect.SendText("n1", "Envíanos la factura."), new Effect.WaitForCustomer("n1", "Factura")], result.Effects);
        Assert.Equal(1, result.State.Attempts);
        Assert.False(result.State.Variables.Has("factura"));
    }

    [Fact]
    public void AnyAttachmentIsSavedWithItsMetadata()
    {
        var flow = Flow(Invoice("n1", "any"), Message("ok", "Recibida"));

        var result = Reply(flow, Start(flow), Attachment("Document", "f.pdf", "application/pdf"));

        Assert.Equal("ok", result.State.CurrentNodeId);
        AssertJson(
            """{ "messageId": "33333333-3333-3333-3333-333333333333", "fileName": "f.pdf", "mimeType": "application/pdf", "stored": false }""",
            result.State.Variables.Get("factura"));
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "n1", Outcome: FlowOutputs.Received });
    }

    [Fact]
    public void TheRestartKeywordRestartsFromTheFirstNode()
    {
        var flow = Flow(Question("q0", saveTo: "motivo", answered: "b"), Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));
        var started = Start(flow, Variables("""{ "nombre": "Ana" }"""));
        var atButtons = Replies(flow, started, Typed("Tengo una falla"), Typed("no sé"));

        Assert.Equal(1, atButtons.State.Attempts);

        var restarted = Reply(flow, atButtons, Typed("  Reiniciar "));

        Assert.Equal<Effect>(
            [new Effect.SendText("q0", "¿Cuál es tu número de documento?"), new Effect.WaitForCustomer("q0", "Número de documento")],
            restarted.Effects);
        Assert.Equal("q0", restarted.State.CurrentNodeId);
        Assert.Equal(0, restarted.State.Attempts);
        Assert.False(restarted.State.Variables.Has("motivo"));
        Assert.Equal("Ana", restarted.State.Variables.Get("nombre")!.GetValue<string>());
        Assert.Equal(new TraceEntry(Now, "b", FlowNodeKinds.Buttons, "restart"), restarted.State.Trace[^2]);

        // En el primer bloque también reinicia (no se guarda como respuesta) y volver a entrar en él no es un ciclo.
        var again = Reply(flow, restarted, Typed("REINICIÁR"));

        Assert.Equal(RunStatus.Waiting, again.State.Status);
        Assert.Equal("q0", again.State.CurrentNodeId);
        Assert.False(again.State.Variables.Has("motivo"));
        Assert.Equal(restarted.State.StepNumber + 1, again.State.StepNumber);
    }

    [Fact]
    public void AnUnsupportedKindCountsAsAnAttempt()
    {
        var sticker = new EngineInput.CustomerMessage("Unsupported", null, null, null);
        var questionFlow = Flow(Question("n1"), Message("ok", "Gracias"));
        var buttonsFlow = Flow(Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));

        AssertResent(Reply(questionFlow, Start(questionFlow), sticker), "n1", attempts: 1);
        AssertResent(Reply(buttonsFlow, Start(buttonsFlow), sticker), "b", attempts: 1);
    }

    [Fact]
    public void APerNodeMaxAttemptsOverridesTheChatbotOne()
    {
        var flow = Flow(Question("n1", "digits", invalid: "bad", maxAttempts: 1), Message("ok", "Gracias"), Message("bad", "No válido"));

        var result = Reply(flow, Start(flow), Typed("abc"));

        Assert.Equal("bad", result.State.CurrentNodeId);
        Assert.Equal(0, result.State.Attempts);
    }

    [Fact]
    public void AnOtherOutputFallbackTakesOtherAtOnce()
    {
        // Ruling P2-R9: con «otherOutput» la primera respuesta inválida ya sale por «other» si está conectada.
        var connected = Flow(Buttons("b", "otherOutput", other: "o"), Message("t", "Técnico"), Message("s", "Ventas"), Message("o", "Te paso"));

        var result = Reply(connected, Start(connected), Typed("nada de eso"));

        Assert.Equal("o", result.State.CurrentNodeId);
        Assert.Equal(new Effect.SendText("o", "Te paso"), result.Effects[0]);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "b", Outcome: FlowOutputs.Other });

        // Sin «other» conectada se comporta como «repeat».
        var unconnected = Flow(Buttons("b", "otherOutput"), Message("t", "Técnico"), Message("s", "Ventas"));

        AssertResent(Reply(unconnected, Start(unconnected), Typed("nada de eso")), "b", attempts: 1);
    }

    [Fact]
    public void AHandoffFallbackHandsOffAtOnce()
    {
        var flow = Flow(EquipmentList("l", fallback: "handoff"), Message("elegido", "Listo"), Message("otra", "Otra"), Message("muchos", "Son muchos"));

        var result = Reply(flow, Start(flow, Equipment(2)), Typed("ninguno"));

        Assert.Equal<Effect>([new Effect.Handoff("l")], result.Effects);
        Assert.Equal(RunStatus.HandedOff, result.State.Status);
        Assert.Equal("l", result.State.CurrentNodeId);
    }

    [Fact]
    public void ADynamicListFitsLongRowsToMetaLimits()
    {
        // 20 letras y un emoji de 4 unidades UTF-16 (👍🏽): el corte no puede partirlo, así que el título queda en 20 letras y «…».
        var longTitle = new string('a', 20) + "👍🏽" + "bbbb";
        var flow = EquipmentFlow(EquipmentList("l", extra: false));

        var result = Start(flow, Equipment(
            new JsonObject { ["id"] = 1, ["referenceCode"] = longTitle, ["address"] = new string('x', 80) },
            new JsonObject { ["id"] = new string('9', 201), ["referenceCode"] = "CAL-2" },
            new JsonObject { ["id"] = 3, ["referenceCode"] = "CAL-3" }));

        var rows = SentRows(result);
        Assert.Equal([("1", new string('a', 20) + "…", (string?)(new string('x', 71) + "…")), ("3", "CAL-3", null)], rows);
        Assert.All(rows, row => Assert.True(row.Title.Length <= 24 && (row.Description?.Length ?? 0) <= 72));
    }

    [Fact]
    public void DuplicateRowIdsKeepTheFirstAndTheExtraWins()
    {
        var flow = EquipmentFlow(EquipmentList("l"));

        var result = Start(flow, Equipment(
            new JsonObject { ["id"] = 1, ["referenceCode"] = "CAL-1" },
            new JsonObject { ["id"] = 1, ["referenceCode"] = "CAL-repetido" },
            new JsonObject { ["id"] = "otra", ["referenceCode"] = "Choca con la extra" },
            new JsonObject { ["id"] = 2, ["referenceCode"] = "CAL-2" }));

        Assert.Equal([("1", "CAL-1", (string?)null), ("2", "CAL-2", null), ("otra", "Otra dirección", null)], SentRows(result));

        // Se depura antes de contar: once elementos con un id repetido son diez filas y la lista sí se manda.
        var eleven = Equipment([.. Enumerable.Range(1, 10).Select(index => new JsonObject { ["id"] = index, ["referenceCode"] = $"CAL-{index}" }),
            new JsonObject { ["id"] = 1, ["referenceCode"] = "CAL-repetido" }]);

        var deduped = Start(EquipmentFlow(EquipmentList("l", extra: false)), eleven);

        Assert.Equal(10, SentRows(deduped).Count);
        Assert.Equal(RunStatus.Waiting, deduped.State.Status);
    }

    [Fact]
    public void TypingTheFullTitleOfAFittedRowChoosesIt()
    {
        const string fullTitle = "Calentador de paso a gas natural";
        var flow = EquipmentFlow(EquipmentList("l"));
        var started = Start(flow, Equipment(new JsonObject { ["id"] = 7, ["referenceCode"] = fullTitle }));

        Assert.Equal("Calentador de paso a ga…", SentRows(started)[0].Title);

        var typed = Reply(flow, started, Typed("calentador de paso a GAS natural"));

        // Se guarda el título completo: es el dato del equipo, el recorte es sólo cómo cupo en WhatsApp.
        Assert.Equal("elegido", typed.State.CurrentNodeId);
        AssertJson(
            """{ "id": "7", "title": "Calentador de paso a gas natural", "item": { "id": 7, "referenceCode": "Calentador de paso a gas natural" } }""",
            typed.State.Variables.Get("equipo"));
        Assert.Equal("elegido", Reply(flow, started, Typed("Calentador de paso a ga…")).State.CurrentNodeId);
    }

    [Fact]
    public void ATappedReplyIsNeverTheRestartKeyword()
    {
        var buttons = new ButtonsNode("b", "Opciones", Out(("again", "a"), ("menu", "m"), (FlowOutputs.Other, null)), "¿Qué hacemos?",
            [new FlowButton("again", "Reiniciar"), new FlowButton("menu", "Menú")], null, InteractiveFallbacks.Repeat);
        var flow = Flow(Question("q0", saveTo: "motivo", answered: "b"), buttons, Message("a", "Otra vez"), Message("m", "Menú"));
        var atButtons = Reply(flow, Start(flow), Typed("Tengo una falla"));

        var tapped = Reply(flow, atButtons, Tapped("again", "Reiniciar"));
        var typed = Reply(flow, atButtons, Typed("Reiniciar"));

        Assert.Equal("a", tapped.State.CurrentNodeId);
        Assert.True(tapped.State.Variables.Has("motivo"));
        Assert.Equal("q0", typed.State.CurrentNodeId);
        Assert.False(typed.State.Variables.Has("motivo"));
    }

    [Fact]
    public void AWaitingRunAtAMissingOrNonWaitingNodeEndsFailed()
    {
        // Ruling P2-R17: como P2-R14, una corrida que espera en un bloque que no existe (o que no espera) termina en vez de quedar muda.
        var flow = Flow(Question("n1"), Message("ok", "Gracias"));
        var started = Start(flow);

        foreach (var nodeId in new[] { "borrado", "ok" })
        {
            var result = FlowEngine.Step(flow, Settings(), started.State with { CurrentNodeId = nodeId }, Typed("10203"), Now);

            Assert.Equal<Effect>([new Effect.EndRun(nodeId, "target_missing")], result.Effects);
            Assert.Equal(RunStatus.Failed, result.State.Status);
        }
    }

    [Theory]
    [InlineData("1.500", "1500")]
    [InlineData("1.500.000", "1500000")]
    [InlineData("1.500,5", "1500.5")]
    [InlineData("1.5", "1.5")]
    [InlineData("3.14", "3.14")]
    [InlineData("1.5000", "1.5000")]
    [InlineData("1234,5", "1234.5")]
    [InlineData("-2,5", "-2.5")]
    [InlineData("0.500", "0.500")]
    public void NumberAnswersReadDotsAsThousandsOnlyInGroupsOfThree(string typed, string expected)
    {
        var flow = Flow(Question("n1", "number", saveTo: "monto"), Message("ok", "Listo"));

        var result = Reply(flow, Start(flow), Typed(typed));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.State.Variables.Get("monto")!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("1,500.5")]
    [InlineData("1.5.3")]
    [InlineData("1e5")]
    [InlineData("doce")]
    public void ANonNumberAnswerToANumberQuestionIsInvalid(string typed)
    {
        var flow = Flow(Question("n1", "number", saveTo: "monto"), Message("ok", "Listo"));

        AssertResent(Reply(flow, Start(flow), Typed(typed)), "n1", attempts: 1);
    }

    [Fact]
    public void ACaptionNeverSelectsAButton()
    {
        var flow = Flow(Buttons("b"), Message("t", "Técnico"), Message("s", "Ventas"));
        var captioned = new EngineInput.CustomerMessage("Image", "Ventas", null, new AttachmentInput(InvoiceMessageId, null, "image/jpeg", false));

        AssertResent(Reply(flow, Start(flow), captioned), "b", attempts: 1);
    }

    [Fact]
    public void ATypedTitleSharedByTwoOptionsIsAmbiguous()
    {
        var list = new ListNode("l", "Sede", Out((FlowOutputs.Chosen, "elegido"), (FlowOutputs.Other, null)), "¿Cuál sede?", "Ver sedes",
            new ListOptions(ListOptionKinds.Fixed, [new ListFixedRow("a", "Sede Norte", null), new ListFixedRow("b", "SEDE NÓRTE", null)],
                null, null, null, null, null),
            "sede", InteractiveFallbacks.Repeat);
        var listFlow = Flow(list, Message("elegido", "Listo"));
        var started = Start(listFlow);

        AssertResent(Reply(listFlow, started, Typed("sede norte")), "l", attempts: 1);
        AssertJson("""{ "id": "b", "title": "SEDE NÓRTE" }""", Reply(listFlow, started, Tapped("b", "SEDE NÓRTE")).State.Variables.Get("sede"));

        var buttons = new ButtonsNode("b", "Confirmar", Out(("yes", "s"), ("yes2", "s"), (FlowOutputs.Other, null)), "¿Confirmas?",
            [new FlowButton("yes", "Sí"), new FlowButton("yes2", "si")], null, InteractiveFallbacks.Repeat);
        var buttonsFlow = Flow(buttons, Message("s", "Listo"));

        AssertResent(Reply(buttonsFlow, Start(buttonsFlow), Typed("SI")), "b", attempts: 1);
    }

    [Fact]
    public void ARegexAnswerIsValidatedWithoutThrowing()
    {
        StepResult Answer(string? pattern, string typed)
        {
            var flow = Flow(Question("n1", QuestionValidationKinds.Regex, saveTo: "codigo", pattern: pattern), Message("ok", "Listo"));
            return Reply(flow, Start(flow), Typed(typed));
        }

        Assert.Equal("ok", Answer("^[A-Z]{3}-[0-9]+$", " CAL-12 ").State.CurrentNodeId);
        AssertResent(Answer("^[A-Z]{3}-[0-9]+$", "cal 12"), "n1", attempts: 1);
        // Un patrón que no compila (sólo en un borrador del simulador) cuenta como intento: el paso no lanza.
        AssertResent(Answer("(", "CAL-12"), "n1", attempts: 1);
    }

    [Fact]
    public void ARowWithoutIdOrTitleIsSkipped()
    {
        var result = Start(EquipmentFlow(EquipmentList("l")), Equipment(
            new JsonObject { ["referenceCode"] = "Sin id" },
            new JsonObject { ["id"] = 2 },
            new JsonObject { ["id"] = 3, ["referenceCode"] = "  " },
            new JsonObject { ["id"] = 4, ["referenceCode"] = "CAL-4" }));

        Assert.Equal([("4", "CAL-4", (string?)null), ("otra", "Otra dirección", null)], SentRows(result));
    }

    [Fact]
    public void AnUnconnectedTooManyEndsCompleted()
    {
        var result = Start(EquipmentFlow(EquipmentList("l", extra: false, tooMany: null)), Equipment(11));

        Assert.Equal<Effect>([new Effect.EndRun("l", "output_unconnected")], result.Effects);
        Assert.Equal(RunStatus.Completed, result.State.Status);
    }

    [Fact]
    public void ExhaustedRepeatButtonsTakeOtherWhenConnected()
    {
        var flow = Flow(Buttons("b", other: "o"), Message("t", "Técnico"), Message("s", "Ventas"), Message("o", "Te paso"));
        var started = Start(flow);

        var first = Reply(flow, started, Typed("x"));
        var second = Reply(flow, first, Typed("y"));
        var third = Reply(flow, second, Typed("z"));

        AssertResent(first, "b", attempts: 1);
        AssertResent(second, "b", attempts: 2);
        Assert.Equal("o", third.State.CurrentNodeId);
        Assert.Contains(third.State.Trace, entry => entry is { NodeId: "b", Outcome: FlowOutputs.Other });
    }

    [Theory]
    [InlineData("image", "Image", true)]
    [InlineData("image", "Document", false)]
    [InlineData("document", "Document", true)]
    [InlineData("any", "Image", true)]
    [InlineData("any", "Document", true)]
    [InlineData("any", "Video", true)]
    [InlineData("any", "Audio", true)]
    [InlineData("any", "Unsupported", false)]
    public void AnAttachmentIsReceivedOnlyWhenItsKindIsAccepted(string accept, string kind, bool received)
    {
        var flow = Flow(Invoice("n1", accept), Message("ok", "Recibida"));

        var result = Reply(flow, Start(flow), Attachment(kind, "archivo", "application/octet-stream"));

        Assert.Equal(received ? "ok" : "n1", result.State.CurrentNodeId);
        Assert.Equal(received, result.State.Variables.Has("factura"));
    }
}
