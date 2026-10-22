using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

public sealed class FlowEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private static FlowDefinition Flow(params FlowNode[] nodes) => new(FlowDefinition.CurrentSchema, new FlowStart(nodes[0].Id), [], nodes);

    private static EngineSettings Settings() => new("reiniciar", 3);

    private static StepResult Start(FlowDefinition flow, RunVariables? variables = null) =>
        FlowEngine.Step(flow, Settings(), RunState.Initial(variables ?? RunVariables.Empty()), new EngineInput.Start(), Now);

    private static StepResult Continue(FlowDefinition flow, StepResult previous, EngineInput input) =>
        FlowEngine.Step(flow, Settings(), previous.State, input, Now);

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);

    private static MessageNode Message(string id, string text, string? next = null, UrlButton? urlButton = null) =>
        new(id, $"Mensaje {id}", Out((FlowOutputs.Next, next)), text, urlButton);

    private static ConditionNode Condition(string id, string match, params ConditionRule[] rules) =>
        new(id, "Condición", Out((FlowOutputs.Yes, "yes"), (FlowOutputs.No, "no")), match, rules);

    private static HttpNode Http(string id, string? ok, string? error) =>
        new(id, "Buscar cliente", Out((FlowOutputs.Ok, ok), (FlowOutputs.Error, error)), Guid.NewGuid(), "GET", "/customers", [], null,
            HttpNode.DefaultTimeoutSeconds, null, [new HttpMapping("data[0]", "cliente")], null);

    private static RunVariables Variables(string json) => RunVariables.Parse(json);

    /// <summary>El id del bloque al que llegó la corrida: en estas pruebas, los mensajes «yes» y «no» de una condición.</summary>
    private static string? Landed(StepResult result) => result.State.CurrentNodeId;

    [Fact]
    public void StartSendsTheMessagesUntilTheFirstWaitingNode()
    {
        var flow = Flow(
            Message("n1", "Hola", next: "n2"),
            new OpenQuestionNode("n2", "Número de documento", Out((FlowOutputs.Answered, null), (FlowOutputs.Invalid, null)),
                "¿Cuál es tu número de documento?", "cedula", new QuestionValidation(QuestionValidationKinds.None, null, null), null));

        var result = Start(flow);

        Assert.Equal<Effect>(
            [
                new Effect.SendText("n1", "Hola"),
                new Effect.SendText("n2", "¿Cuál es tu número de documento?"),
                new Effect.WaitForCustomer("n2", "Número de documento"),
            ],
            result.Effects);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal("n2", result.State.CurrentNodeId);
        Assert.Equal(2, result.State.StepNumber);
        Assert.Equal(0, result.State.Attempts);
    }

    [Fact]
    public void AMessageWithUrlButtonIsACtaUrl()
    {
        var flow = Flow(Message("n1", "Hola {nombre}", urlButton: new UrlButton("Ver orden", "https://www.example.com/ordenes/{orden.id}")));

        var result = Start(flow, Variables("""{ "nombre": "Ana", "orden": { "id": 42 } }"""));

        var send = Assert.IsType<Effect.SendInteractive>(result.Effects[0]);
        Assert.Equal(new InteractiveMessage.CtaUrl("Hola Ana", "Ver orden", "https://www.example.com/ordenes/42"), send.Message);
        Assert.Equal("n1", send.NodeId);
    }

    [Fact]
    public void AnUnconnectedOutputEndsCompleted()
    {
        var result = Start(Flow(Message("n1", "Hola")));

        Assert.Equal<Effect>([new Effect.SendText("n1", "Hola"), new Effect.EndRun("n1", "output_unconnected")], result.Effects);
        Assert.Equal(RunStatus.Completed, result.State.Status);
    }

    [Fact]
    public void ConditionAllAndAnyRouteYesOrNo()
    {
        var variables = """{ "cliente": { "id": 1 }, "ciudad": "Medellín" }""";
        var found = new ConditionRule("cliente", "isNotEmpty", null);
        var bogota = new ConditionRule("ciudad", "equals", "Bogota");
        var medellin = new ConditionRule("ciudad", "equals", "MEDELLIN");

        StepResult Run(ConditionNode condition) =>
            Start(Flow(condition, Message("yes", "sí"), Message("no", "no")), Variables(variables));

        Assert.Equal("yes", Landed(Run(Condition("c", "all", found))));
        Assert.Equal("yes", Landed(Run(Condition("c", "any", bogota, medellin))));
        Assert.Equal("no", Landed(Run(Condition("c", "all", bogota, medellin))));
        Assert.Equal("no", Landed(Run(Condition("c", "all", new ConditionRule("cliente", "isEmpty", null)))));
        Assert.Equal("yes", Landed(Run(Condition("c", "all", new ConditionRule("nada", "isEmpty", null)))));
        Assert.Equal("yes", Landed(Run(Condition("c", "all", new ConditionRule("ciudad", "contains", "dell")))));
        Assert.Equal("yes", Landed(Run(Condition("c", "all", new ConditionRule("ciudad", "notEquals", "Cali")))));
        Assert.Equal("yes", Landed(Run(Condition("c", "all", new ConditionRule("cliente.id", "greaterThan", "0.5")))));
        Assert.Equal("no", Landed(Run(Condition("c", "all", new ConditionRule("cliente.id", "lessThan", "1")))));
        // Un texto que no es número no se compara: la regla no se cumple ni por un lado ni por el otro.
        Assert.Equal("no", Landed(Run(Condition("c", "any",
            new ConditionRule("ciudad", "greaterThan", "1"), new ConditionRule("ciudad", "lessThan", "1")))));
    }

    [Fact]
    public void ConditionCountOperatorsReadArrays()
    {
        var variables = """{ "equipos": ["a", "b"], "cliente": { "id": 1, "nombre": "Ana" } }""";

        StepResult Run(ConditionRule rule) =>
            Start(Flow(Condition("c", "all", rule), Message("yes", "sí"), Message("no", "no")), Variables(variables));

        Assert.Equal("yes", Landed(Run(new ConditionRule("equipos", "countGreaterThan", "1"))));
        Assert.Equal("yes", Landed(Run(new ConditionRule("equipos", "countEquals", "2"))));
        Assert.Equal("no", Landed(Run(new ConditionRule("cliente", "countGreaterThan", "0"))));
        Assert.Equal("yes", Landed(Run(new ConditionRule("cliente", "countEquals", "0"))));
    }

    [Fact]
    public void BusinessHoursWaitsForTheAnswerAndRoutes()
    {
        var flow = Flow(
            new BusinessHoursNode("n1", "Horario", Out((FlowOutputs.Open, "open"), (FlowOutputs.Closed, "closed"))),
            Message("open", "Abiertos"),
            Message("closed", "Cerrados"));

        var started = Start(flow);

        Assert.Equal<Effect>([new Effect.CheckBusinessHours("n1")], started.Effects);
        Assert.Equal(RunStatus.Running, started.State.Status);
        Assert.Equal("n1", started.State.CurrentNodeId);

        var closed = Continue(flow, started, new EngineInput.BusinessHours("n1", IsOpen: false));

        Assert.Equal<Effect>([new Effect.SendText("closed", "Cerrados"), new Effect.EndRun("closed", "output_unconnected")], closed.Effects);
        Assert.Equal(RunStatus.Completed, closed.State.Status);
    }

    [Fact]
    public void HttpOkMergesMappedVariablesAndRoutesOk()
    {
        var http = Http("n1", ok: "n2", error: "n3");
        var flow = Flow(http, Message("n2", "Cliente {cliente.id}"), Message("n3", "Falló"));

        var started = Start(flow);

        Assert.Equal<Effect>([new Effect.CallHttp("n1", http)], started.Effects);
        Assert.Equal(RunStatus.Running, started.State.Status);
        Assert.Equal("n1", started.State.CurrentNodeId);

        var result = Continue(flow, started, new EngineInput.HttpResult("n1", Ok: true, 200, Variables("""{ "cliente": { "id": 7 } }"""), null));

        Assert.Equal(new Effect.SendText("n2", "Cliente 7"), result.Effects[0]);
        Assert.Equal(7, result.State.Variables.Get("cliente.id")!.GetValue<int>());
        // El paso anterior no cambió: el motor trabaja sobre una copia de las variables.
        Assert.False(started.State.Variables.Has("cliente"));
    }

    [Fact]
    public void HttpErrorRoutesError()
    {
        var flow = Flow(Http("n1", ok: "n2", error: "n3"), Message("n2", "Encontrado"), Message("n3", "Falló"));

        var result = Continue(flow, Start(flow),
            new EngineInput.HttpResult("n1", Ok: false, 500, Variables("""{ "cliente": { "id": 7 } }"""), "http.status_not_ok"));

        Assert.Equal(new Effect.SendText("n3", "Falló"), result.Effects[0]);
        Assert.False(result.State.Variables.Has("cliente"));
    }

    [Fact]
    public void AStaleHttpResultIsIgnored()
    {
        var flow = Flow(Http("n1", ok: "n2", error: null), Message("n2", "Encontrado"));
        var started = Start(flow);

        var result = Continue(flow, started, new EngineInput.HttpResult("otro", Ok: true, 200, RunVariables.Empty(), null));

        Assert.Same(started.State, result.State);
        Assert.Empty(result.Effects);
        Assert.Empty(Continue(flow, started, new EngineInput.BusinessHours("n1", IsOpen: true)).Effects);
    }

    [Fact]
    public void HandoffSendsTheOptionalTextAndEnds()
    {
        var withText = Start(Flow(new HandoffNode("n1", "Asesor", Out(), "Te paso con {nombre}.")), Variables("""{ "nombre": "un asesor" }"""));

        Assert.Equal<Effect>([new Effect.SendText("n1", "Te paso con un asesor."), new Effect.Handoff("n1")], withText.Effects);
        Assert.Equal(RunStatus.HandedOff, withText.State.Status);

        var withoutText = Start(Flow(new HandoffNode("n1", "Asesor", Out(), null)));

        Assert.Equal<Effect>([new Effect.Handoff("n1")], withoutText.Effects);
    }

    [Fact]
    public void ResolveCarriesTheReasonId()
    {
        var reasonId = Guid.NewGuid();

        var result = Start(Flow(new ResolveNode("n1", "Listo", Out(), "Tu orden es la {orden.id}.", reasonId)),
            Variables("""{ "orden": { "id": "A-9" } }"""));

        Assert.Equal<Effect>([new Effect.SendText("n1", "Tu orden es la A-9."), new Effect.Resolve("n1", reasonId)], result.Effects);
        Assert.Equal(RunStatus.Resolved, result.State.Status);
    }

    [Fact]
    public void TraceIsCappedAtTwoHundred()
    {
        var nodes = Enumerable.Range(1, 250)
            .Select(index => (FlowNode)Message($"n{index}", $"Mensaje {index}", next: index < 250 ? $"n{index + 1}" : null))
            .ToArray();

        var result = Start(Flow(nodes));

        Assert.Equal(RunState.TraceMax, result.State.Trace.Count);
        Assert.Equal("n51", result.State.Trace[0].NodeId);
        Assert.Equal("n250", result.State.Trace[^1].NodeId);
        Assert.Equal(250, result.State.StepNumber);
        Assert.All(result.State.Trace, entry => Assert.Equal(Now, entry.At));
    }

    [Fact]
    public void TheAnnexAFlowStartsWithTheWelcomeAndWaitsForTheDocument()
    {
        var variables = RunVariables.Empty();
        variables.Set("nombre", JsonValue.Create("Ana"));

        var result = Start(AnnexAFlow.Parse(), variables);

        Assert.Equal<Effect>(
            [
                new Effect.SendInteractive("n1", new InteractiveMessage.CtaUrl(
                    "Hola Ana, bienvenido a la línea de servicio.", "Registrarme", "https://www.example.com/registro")),
                new Effect.SendText("n2", "¿Cuál es tu número de documento?"),
                new Effect.WaitForCustomer("n2", "Número de documento"),
            ],
            result.Effects);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal("n2", result.State.CurrentNodeId);
        Assert.Equal(2, result.State.StepNumber);
        Assert.Equal(["n1", "n2"], result.State.Trace.Select(entry => entry.NodeId));
    }
}
