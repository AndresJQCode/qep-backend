using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §6.1, §6.3, D-B4, D-B12 y D-B13: el motor entra al bloque «Agente IA», espera con la
/// conversación delegada y sale por la salida que devuelve el agente.</summary>
public sealed class FlowEngineAgentBlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid AgentId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
    private static readonly Guid Advisor = Guid.Parse("c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1");

    [Fact]
    public void EnteringTheBlockAsksToDelegateAndWaitsDelegated()
    {
        var result = Start(Flow(Agent()), Variables("""{ "nombre_contacto": "Laura" }"""));

        Assert.Equal<Effect>(
            [new Effect.DelegateToAgent("ai", AgentId, "Resuelve dudas.", "El cliente se llama Laura.", "lastMessage", 60)],
            result.Effects);
        Assert.Equal((RunStatus.Waiting, "ai"), (result.State.Status, result.State.CurrentNodeId));
        Assert.Equal(TraceOutcomes.Delegated, result.State.Trace[^1].Outcome);
        Assert.True(result.State.IsDelegated);
    }

    [Theory]
    [InlineData("done", "m-done")]
    [InlineData("human", "m-human")]
    [InlineData("noAnswer", "m-no-answer")]
    [InlineData("idle", "m-idle")]
    [InlineData("error", "m-error")]
    public void EachOutputMergesTheBlockVariables(string output, string next)
    {
        var flow = Flow(
            Agent() with
            {
                Outputs = Out(("done", "m-done"), ("human", "m-human"), ("noAnswer", "m-no-answer"), ("idle", "m-idle"), ("error", "m-error")),
            },
            Message("m-done"), Message("m-human"), Message("m-no-answer"), Message("m-idle"), Message("m-error"));
        var waiting = Start(flow);

        var result = Step(flow, waiting, new EngineInput.AgentResult("ai", Guid.NewGuid(), output, "objective_completed", "Quiere la talla M", "COT-12"));

        Assert.Equal(next, result.State.CurrentNodeId);
        Assert.Equal(output, result.State.Trace.Single(entry => entry.NodeId == "ai" && entry.Outcome != TraceOutcomes.Delegated).Outcome);
        var saved = Assert.IsType<JsonObject>(result.State.Variables.Get("asesorIa"));
        Assert.Equal(
            ("Quiere la talla M", "objective_completed", "COT-12"),
            (saved["summary"]!.GetValue<string>(), saved["reason"]!.GetValue<string>(), saved["quotationNumber"]!.GetValue<string>()));
    }

    [Fact]
    public void AMissingSummaryOrQuotationIsSavedEmpty()
    {
        var flow = Flow(Agent(), Message("end"));

        var result = Step(flow, Start(flow), new EngineInput.AgentResult("ai", Guid.NewGuid(), "done", "objective_completed"));

        Assert.Equal(string.Empty, result.State.Variables.TextOf("asesorIa.summary"));
        Assert.Equal(string.Empty, result.State.Variables.TextOf("asesorIa.quotationNumber"));
    }

    [Fact]
    public void AResultOfAnotherNodeChangesNothing()
    {
        var flow = Flow(Agent(), Message("end"));
        var waiting = Start(flow);

        var other = Step(flow, waiting, new EngineInput.AgentResult("other", Guid.NewGuid(), "done", "objective_completed"));
        var notDelegated = FlowEngine.Step(
            flow, Settings(), waiting.State with { Status = RunStatus.Running }, new EngineInput.AgentResult("ai", Guid.NewGuid(), "done", "x"), Now);

        Assert.Same(waiting.State, other.State);
        Assert.Empty(other.Effects);
        Assert.Empty(notDelegated.Effects);
    }

    [Theory]
    [InlineData("human")]
    [InlineData("noAnswer")]
    [InlineData("error")]
    public void UnconnectedProblemOutputsHandOff(string output)
    {
        var flow = Flow(Agent() with { Outputs = Out(("done", "end"), ("human", null), ("noAnswer", null), ("idle", null), ("error", null)) }, Message("end"));

        var result = Step(flow, Start(flow), new EngineInput.AgentResult("ai", Guid.NewGuid(), output, "customer_asked"));

        Assert.Equal<Effect>([new Effect.Handoff("ai", HandoffReasons.AgentBlockOutputUnhandled, null, output)], result.Effects);
        Assert.Equal(RunStatus.HandedOff, result.State.Status);
        Assert.Equal("customer_asked", result.State.Variables.TextOf("asesorIa.reason"));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("idle")]
    public void UnconnectedDoneOrIdleEndTheRun(string output)
    {
        var flow = Flow(Agent() with { Outputs = Out(("done", null), ("human", "end"), ("noAnswer", "end"), ("idle", null), ("error", "end")) }, Message("end"));

        var result = Step(flow, Start(flow), new EngineInput.AgentResult("ai", Guid.NewGuid(), output, "idle"));

        Assert.Equal<Effect>([new Effect.EndRun("ai", EndReasons.OutputUnconnected)], result.Effects);
        Assert.Equal(RunStatus.Completed, result.State.Status);
    }

    // D-B13: con un borrador de cotización y sin camino por «human», la conversación va a la asesora del borrador.
    [Fact]
    public void ADraftWithoutHumanPathGoesToTheAdvisor()
    {
        var flow = Flow(Agent() with { Outputs = Out(("done", "end"), ("human", null), ("noAnswer", "end"), ("idle", "end"), ("error", "end")) }, Message("end"));

        var result = Step(
            flow, Start(flow),
            new EngineInput.AgentResult("ai", Guid.NewGuid(), "human", "quotation_draft_created", null, "COT-7", Advisor));

        Assert.Equal<Effect>([new Effect.Handoff("ai", HandoffReasons.AgentBlockOutputUnhandled, Advisor, "human")], result.Effects);
        Assert.Equal("COT-7", result.State.Variables.TextOf("asesorIa.quotationNumber"));
    }

    // §6.4: ventana cerrada o contacto bloqueado; no hay a quién mandarle nada.
    [Theory]
    [InlineData("window_closed", RunStatus.WindowClosed, "window_closed")]
    [InlineData("blocked", RunStatus.Interrupted, "interrupted")]
    public void ANullOutputEndsTheRunWithoutLeaving(string reason, RunStatus status, string endReason)
    {
        var flow = Flow(Agent(), Message("end"));

        var result = Step(flow, Start(flow), new EngineInput.AgentResult("ai", Guid.NewGuid(), null, reason));

        Assert.Equal<Effect>([new Effect.EndRun("ai", endReason)], result.Effects);
        Assert.Equal((status, "ai"), (result.State.Status, result.State.CurrentNodeId));
        Assert.Null(result.State.Variables.Get("asesorIa"));
    }

    [Fact]
    public void AMissingAgentLeavesThroughError()
    {
        var flow = Flow(Agent() with { AgentId = null }, Message("end"));

        var result = Start(flow);

        Assert.DoesNotContain(result.Effects, effect => effect is Effect.DelegateToAgent);
        Assert.Equal<Effect>([new Effect.SendText("end", "Hola"), new Effect.EndRun("end", EndReasons.OutputUnconnected)], result.Effects);
        Assert.Equal("agent_missing", result.State.Variables.TextOf("asesorIa.reason"));
        Assert.Equal(FlowOutputs.Error, result.State.Trace.Single(entry => entry.NodeId == "ai").Outcome);
    }

    [Fact]
    public void TheContextIsInterpolatedAndCut()
    {
        var flow = Flow(Agent() with { Context = "{largo}😀" });
        var variables = Variables($$"""{ "largo": "{{new string('x', 1999)}}" }""");

        var delegation = Assert.IsType<Effect.DelegateToAgent>(Assert.Single(Start(flow, variables).Effects));

        // 1999 + el emoji (dos unidades) pasa de 2000: se corta sin partir el par sustituto.
        Assert.Equal(new string('x', 1999), delegation.Context);
    }

    private static AiAgentNode Agent() =>
        new(
            "ai", "Asesor IA", Out(("done", "end"), ("human", "end"), ("noAnswer", "end"), ("idle", "end"), ("error", "end")), AgentId,
            "Resuelve dudas.", "El cliente se llama {nombre_contacto}.", "lastMessage", 60, "asesorIa");

    private static MessageNode Message(string id) => new(id, $"Mensaje {id}", Out((FlowOutputs.Next, null)), "Hola", null);

    private static FlowDefinition Flow(params FlowNode[] nodes) => new(FlowDefinition.CurrentSchema, new FlowStart(nodes[0].Id), [], nodes);

    private static EngineSettings Settings() => new("reiniciar", 3);

    private static StepResult Start(FlowDefinition flow, RunVariables? variables = null) =>
        FlowEngine.Step(flow, Settings(), RunState.Initial(variables ?? RunVariables.Empty()), new EngineInput.Start(), Now);

    private static StepResult Step(FlowDefinition flow, StepResult previous, EngineInput input) =>
        FlowEngine.Step(flow, Settings(), previous.State, input, Now);

    private static RunVariables Variables(string json) => RunVariables.Parse(json);

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);
}
