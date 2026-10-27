using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-25 D-E5, D-E9 y plan D-P1..D-P3: el motor devuelve un registro por bloque visitado.</summary>
public sealed class FlowEngineStepRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);

    private static FlowDefinition Flow(string first, params FlowNode[] nodes) => new(FlowDefinition.CurrentSchema, new FlowStart(first), [], nodes);

    private static MessageNode Message(string id, string text, string? next = null) => new(id, $"Mensaje {id}", Out((FlowOutputs.Next, next)), text, null);

    private static OpenQuestionNode Question(string id, string? answered) =>
        new(id, "Documento", Out((FlowOutputs.Answered, answered), (FlowOutputs.Invalid, null)), "¿Cuál es tu documento?", "doc",
            new QuestionValidation(QuestionValidationKinds.None, null, null), null);

    private static HttpNode Http(string id, string? ok) =>
        new(id, "Buscar cliente", Out((FlowOutputs.Ok, ok), (FlowOutputs.Error, null)), Guid.NewGuid(), "GET", "/customers", [], null,
            HttpNode.DefaultTimeoutSeconds, null, [new HttpMapping("data[0]", "cliente")], null);

    private static ButtonsNode Buttons(string id) =>
        new(id, "Motivo", Out(("tech", null), ("sales", null), (FlowOutputs.Other, null)), "¿En qué te ayudamos?",
            [new FlowButton("tech", "Servicio técnico"), new FlowButton("sales", "Ventas")], "motivo", InteractiveFallbacks.Repeat, null);

    private static StepResult Start(FlowDefinition flow) =>
        FlowEngine.Step(flow, Settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), Now);

    private static StepResult Next(FlowDefinition flow, StepResult previous, EngineInput input) =>
        FlowEngine.Step(flow, Settings, previous.State, input, Now);

    private static EngineInput.CustomerMessage Typed(string text) => new("Text", text, null, null);

    private static void AssertRecord(
        StepRecord record, int step, int seq, string nodeId, string kind, string outcome, string? fromNodeId, string? fromOutput, bool reset = false)
    {
        Assert.Equal((step, seq, nodeId, kind, outcome), (record.Step, record.Seq, record.NodeId, record.Kind, record.Outcome));
        Assert.Equal((fromNodeId, fromOutput), (record.FromNodeId, record.FromOutput));
        Assert.Equal(reset, record.Reset);
    }

    [Fact]
    public void AWaitingBlockAndItsExitShareTheStepWithDistinctSeq()
    {
        var flow = Flow("q", Question("q", "m"), Message("m", "Gracias"));

        var started = Start(flow);
        var asked = Assert.Single(started.Steps);
        AssertRecord(asked, 1, StepSeqs.Entry, "q", "openQuestion", "waiting", "start", "next");
        Assert.Equal([0], asked.SendIndexes);

        var answered = Next(flow, started, Typed("123"));
        Assert.Equal(2, answered.Steps.Count);
        AssertRecord(answered.Steps[0], 1, StepSeqs.Exit, "q", "openQuestion", "answered", null, null);
        var write = Assert.Single(answered.Steps[0].Writes);
        Assert.Equal("doc", write.Name);
        Assert.Equal("123", write.Value!.GetValue<string>());
        AssertRecord(answered.Steps[1], 2, StepSeqs.Entry, "m", "message", "next", "q", "answered");
    }

    [Fact]
    public void MergedHttpVariablesAreWritesOfTheExit()
    {
        var flow = Flow("http", Http("http", "m"), Message("m", "Hola"));
        var pending = Start(flow);
        var mapped = RunVariables.Parse("""{"cliente":{"id":7}}""");

        var ok = Next(flow, pending, new EngineInput.HttpResult("http", true, 200, mapped, null));
        var exit = ok.Steps[0];
        AssertRecord(exit, 1, StepSeqs.Exit, "http", "http", "ok", null, null);
        var write = Assert.Single(exit.Writes);
        Assert.Equal("cliente", write.Name);
        Assert.Equal("""{"id":7}""", write.Value!.ToJsonString());

        var failed = Next(flow, pending, new EngineInput.HttpResult("http", false, 500, mapped, "boom"));
        Assert.Equal("error", failed.Steps[0].Outcome);
        Assert.Empty(failed.Steps[0].Writes);
    }

    [Fact]
    public void TheRestartMarksReset()
    {
        var flow = Flow("q", Question("q", "m"), Message("m", "Gracias"));
        var waiting = Start(flow);

        var restarted = Next(flow, waiting, Typed("reiniciar"));

        Assert.Equal(2, restarted.Steps.Count);
        AssertRecord(restarted.Steps[0], 1, StepSeqs.Exit, "q", "openQuestion", "restart", null, null, reset: true);
        AssertRecord(restarted.Steps[1], 2, StepSeqs.Entry, "q", "openQuestion", "waiting", "start", "next");
    }

    [Fact]
    public void SendsPointToTheirEffects()
    {
        var flow = Flow("m", Message("m", "Hola", "q"), Question("q", null));

        var started = Start(flow);

        Assert.Equal([0], started.Steps[0].SendIndexes);
        Assert.Equal([1], started.Steps[1].SendIndexes);
        Assert.Equal(new Effect.SendText("m", "Hola"), started.Effects[0]);
    }

    [Fact]
    public void AResendAfterAnInvalidReplyHasNoOrigin()
    {
        var flow = Flow("b", Buttons("b"));
        var waiting = Start(flow);

        var resent = Next(flow, waiting, Typed("otra cosa"));

        var record = Assert.Single(resent.Steps);
        AssertRecord(record, 2, StepSeqs.Entry, "b", "buttons", "waiting", null, null);
        Assert.Equal([0], record.SendIndexes);
    }

    [Fact]
    public void AHandoffFallbackExitsTheBlockWithTheHandedOffOutcomeAndNoOrigin()
    {
        var buttons = new ButtonsNode(
            "b", "Motivo", Out(("tech", null), ("sales", null), (FlowOutputs.Other, null)), "¿En qué te ayudamos?",
            [new FlowButton("tech", "Servicio técnico"), new FlowButton("sales", "Ventas")], "motivo", InteractiveFallbacks.Handoff);
        var flow = Flow("b", buttons);
        var waiting = Start(flow);

        var handedOff = Next(flow, waiting, Typed("otra cosa"));

        var record = Assert.Single(handedOff.Steps);
        AssertRecord(record, 1, StepSeqs.Exit, "b", "buttons", TraceOutcomes.HandedOff, null, null);
        Assert.Empty(record.SendIndexes);
    }

    [Fact]
    public void ExhaustedAttemptsLeavingByInvalidExitTheBlockThenEnterTheNextWithItsOrigin()
    {
        var question = new OpenQuestionNode(
            "q", "Documento", Out((FlowOutputs.Answered, null), (FlowOutputs.Invalid, "m")), "¿Cuál es tu documento?", "doc",
            new QuestionValidation(QuestionValidationKinds.Digits, null, null), 1);
        var flow = Flow("q", question, Message("m", "Sigamos"));
        var waiting = Start(flow);

        var left = Next(flow, waiting, Typed("abc"));

        Assert.Equal(2, left.Steps.Count);
        AssertRecord(left.Steps[0], 1, StepSeqs.Exit, "q", "openQuestion", FlowOutputs.Invalid, null, null);
        AssertRecord(left.Steps[1], 2, StepSeqs.Entry, "m", "message", "next", "q", FlowOutputs.Invalid);
    }

    [Fact]
    public void TheConsentQuestionIsAnEntryOfItsOwnStep()
    {
        var flow = Flow("dc", new DataConsentNode("dc", "Política", Out((FlowOutputs.Accepted, null), (FlowOutputs.Rejected, null)), "Acepta"));

        var started = Start(flow);
        AssertRecord(Assert.Single(started.Steps), 1, StepSeqs.Entry, "dc", "dataConsent", "pending", "start", "next");

        var asked = Next(flow, started, new EngineInput.DataConsent("dc", Accepted: false));
        var record = Assert.Single(asked.Steps);
        AssertRecord(record, 2, StepSeqs.Entry, "dc", "dataConsent", "waiting", null, null);
        Assert.Equal([0], record.SendIndexes);
    }

    [Fact]
    public void ARecordedWalkKeepsTheTraceUnchanged()
    {
        var flow = Flow("q", Question("q", "m"), Message("m", "Gracias"));
        var started = Start(flow);
        Assert.Equal([new TraceEntry(Now, "q", "openQuestion", "waiting")], started.State.Trace);

        var answered = Next(flow, started, Typed("123"));
        Assert.Equal(
            [
                new TraceEntry(Now, "q", "openQuestion", "waiting"),
                new TraceEntry(Now, "q", "openQuestion", "answered"),
                new TraceEntry(Now, "m", "message", "next"),
            ],
            answered.State.Trace);

        var restarted = Next(flow, started, Typed("reiniciar"));
        Assert.Equal(
            [
                new TraceEntry(Now, "q", "openQuestion", "waiting"),
                new TraceEntry(Now, "q", "openQuestion", "restart"),
                new TraceEntry(Now, "q", "openQuestion", "waiting"),
            ],
            restarted.State.Trace);

        var buttons = Flow("b", Buttons("b"));
        var resent = Next(buttons, Start(buttons), Typed("otra cosa"));
        Assert.Equal(
            [new TraceEntry(Now, "b", "buttons", "waiting"), new TraceEntry(Now, "b", "buttons", "waiting")], resent.State.Trace);
    }
}
