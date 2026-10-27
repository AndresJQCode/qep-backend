using Modules.Automations.Application.Engine;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-25 D-E6 y plan D-P3: el registro en memoria de una unidad del procesador.</summary>
public sealed class RunUnitLogTests
{
    private static readonly RunState State = RunState.Initial(RunVariables.Empty());

    private static StepRecord Record(int step, params int[] sends) =>
        new(step, StepSeqs.Entry, $"n{step}", "Message", "Sent", [], sends, false, null, null);

    private static StepResult TwoSends() =>
        new(State, [new Effect.SendText("n1", "hola"), new Effect.SendInteractive("n2", new InteractiveMessage.CtaUrl("¿?", "Ver", "https://x"))])
        {
            Steps = [Record(1, 0), Record(2, 1)],
        };

    [Fact]
    public void ClientIdsLandOnTheStepThatSent()
    {
        var log = new RunUnitLog();
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        log.Record(TwoSends(), new EngineInput.Start());

        log.Sent(1, g2, null);
        log.Sent(0, g1, 7);

        var first = Assert.Single(log.Steps[0].Sends);
        Assert.Equal(new RunStepSend("Text", g1, 7), first);
        var second = Assert.Single(log.Steps[1].Sends);
        Assert.Equal(new RunStepSend("Interactive", g2, null), second);
    }

    [Fact]
    public void ASendNoRecordClaimsIsIgnored()
    {
        var log = new RunUnitLog();
        log.Record(TwoSends(), new EngineInput.Start());

        log.Sent(5, Guid.NewGuid(), null);
        log.Sent(-1, Guid.NewGuid(), null);

        Assert.All(log.Steps, step => Assert.Empty(step.Sends));
    }

    [Fact]
    public void TheInboundGoesToTheFirstRecordOfItsStep()
    {
        var log = new RunUnitLog();
        var m = Guid.NewGuid();

        log.Record(TwoSends(), new EngineInput.CustomerMessage("Text", "hi", null, null, MessageId: m));

        Assert.Equal(m, log.Steps[0].InputMessageId);
        Assert.Null(log.Steps[1].InputMessageId);
    }

    [Fact]
    public void AnHttpResultAttachesNoInput()
    {
        var log = new RunUnitLog();

        log.Record(TwoSends(), new EngineInput.HttpResult("n1", true, 200, RunVariables.Empty(), null));

        Assert.All(log.Steps, step => Assert.Null(step.InputMessageId));
    }
}
