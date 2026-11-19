using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-19 (chatbot CRM action) «Execution»: the «Acción de CRM» block in the engine.</summary>
public sealed class FlowEngineCrmActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);

    private static readonly CrmActionNode Crm = new(
        "crm", "Etiquetar", Out((FlowOutputs.Ok, "ok"), (FlowOutputs.Skipped, "skipped"), (FlowOutputs.Error, "error")),
        CrmOperations.AddTag, Guid.Parse("7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a"), null, "crm");

    private static readonly FlowDefinition Flow = new(
        FlowDefinition.CurrentSchema, new FlowStart("crm"), [],
        [
            Crm,
            new MessageNode("ok", "Mensaje ok", Out((FlowOutputs.Next, null)), "Listo {crm.result}", null),
            new MessageNode("skipped", "Mensaje skipped", Out((FlowOutputs.Next, null)), "Nada {crm.result}", null),
            new MessageNode("error", "Mensaje error", Out((FlowOutputs.Next, null)), "Falló {crm.result}", null),
        ]);

    private static StepResult Step(RunState state, EngineInput input) => FlowEngine.Step(Flow, Settings, state, input, Now);

    private static StepResult Start() => Step(RunState.Initial(RunVariables.Parse("""{"nombre_contacto":"Laura"}""")), new EngineInput.Start());

    [Fact]
    public void EnteringEmitsTheEffectAndStopsRunning()
    {
        var result = Start();

        Assert.Equal<Effect>([new Effect.CrmAction("crm", Crm)], result.Effects);
        Assert.Equal(RunStatus.Running, result.State.Status);
        Assert.Equal("crm", result.State.CurrentNodeId);
        Assert.Equal(new TraceEntry(Now, "crm", FlowNodeKinds.CrmAction, TraceOutcomes.Pending), result.State.Trace[^1]);
    }

    [Theory]
    [InlineData("added", "ok", "Listo added")]
    [InlineData("no_open_deal", "skipped", "Nada no_open_deal")]
    [InlineData("tag_limit", "error", "Falló tag_limit")]
    public void TheCodeLeavesByItsOutput(string code, string output, string sent)
    {
        var result = Step(Start().State, new EngineInput.CrmResult("crm", code));

        Assert.Equal(new Effect.SendText(output, sent), result.Effects[0]);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "crm" } && entry.Outcome == output);
    }

    [Fact]
    public void OkCodeLeavesByOk() => AssertLeavesBy("added", FlowOutputs.Ok);

    [Fact]
    public void SkippedCodeLeavesBySkipped() => AssertLeavesBy("no_open_deal", FlowOutputs.Skipped);

    [Fact]
    public void ErrorCodeLeavesByError() => AssertLeavesBy("tag_limit", FlowOutputs.Error);

    [Fact]
    public void ResultIsWrittenToSaveToDotResult()
    {
        var result = Step(Start().State, new EngineInput.CrmResult("crm", "already_tagged"));

        Assert.Equal("already_tagged", result.State.Variables.TextOf("crm.result"));
        var exit = Assert.Single(result.Steps, step => step.NodeId == "crm");
        Assert.Contains(exit.Writes, write => write.Name == "crm");
    }

    [Fact]
    public void ResultForAnotherNodeIsIgnored()
    {
        var pending = Start().State;

        var result = Step(pending, new EngineInput.CrmResult("other", "added"));

        Assert.Same(pending, result.State);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public void UnknownCodeLeavesByError()
    {
        var result = Step(Start().State, new EngineInput.CrmResult("crm", "weird"));

        Assert.Equal(new Effect.SendText("error", "Falló weird"), result.Effects[0]);
        Assert.Equal("weird", result.State.Variables.TextOf("crm.result"));
    }

    private static void AssertLeavesBy(string code, string output)
    {
        var result = Step(Start().State, new EngineInput.CrmResult("crm", code));

        Assert.Equal(output, Assert.IsType<Effect.SendText>(result.Effects[0]).NodeId);
    }
}
