using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-26 D-2, D-4, D-6 y D-7: el bloque Política de datos en el motor.</summary>
public sealed class FlowEngineDataConsentTests
{
    private const string ConsentText = "Hola {nombre}, lee https://e.co/d";
    private const string ShownToLaura = "Hola Laura, lee https://e.co/d";

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid PromptId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Member = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly EngineSettings Settings = new("reiniciar", 3);

    private static Dictionary<string, string?> Out(params (string Name, string? Target)[] outputs) =>
        outputs.ToDictionary(output => output.Name, output => output.Target, StringComparer.Ordinal);

    private static readonly FlowDefinition Flow = new(
        FlowDefinition.CurrentSchema, new FlowStart("consent"), [],
        [
            new DataConsentNode("consent", "Política de datos", Out((FlowOutputs.Accepted, "yes"), (FlowOutputs.Rejected, "no")), ConsentText),
            new MessageNode("yes", "Mensaje yes", Out((FlowOutputs.Next, null)), "Gracias", null),
            new MessageNode("no", "Mensaje no", Out((FlowOutputs.Next, null)), "Entendido", null),
        ]);

    private static RunVariables Laura() => RunVariables.Parse("""{"nombre":"Laura"}""");

    private static StepResult Step(RunState state, EngineInput input, EngineSettings? settings = null) =>
        FlowEngine.Step(Flow, settings ?? Settings, state, input, Now);

    private static StepResult Start(EngineSettings? settings = null) => Step(RunState.Initial(Laura()), new EngineInput.Start(), settings);

    /// <summary>La corrida con la pregunta ya enviada: el cliente no había aceptado.</summary>
    private static StepResult Asked(EngineSettings? settings = null) =>
        Step(Start(settings).State, new EngineInput.DataConsent("consent", Accepted: false), settings);

    private static EngineInput.CustomerMessage Typed(string text) => new("Text", text, null, null);

    private static EngineInput.CustomerMessage Tapped(string id, string title) => new("Interactive", title, id, null);

    private static Effect.RecordDataConsent Recorded(StepResult result) => Assert.IsType<Effect.RecordDataConsent>(result.Effects[0]);

    private static void AssertAsked(StepResult result, int attempts)
    {
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal("consent", result.State.CurrentNodeId);
        Assert.Equal(attempts, result.State.Attempts);
        Assert.Equal(2, result.Effects.Count);
        var send = Assert.IsType<Effect.SendInteractive>(result.Effects[0]);
        Assert.Equal("consent", send.NodeId);
        var buttons = Assert.IsType<InteractiveMessage.Buttons>(send.Message);
        Assert.Equal(ShownToLaura, buttons.Body);
        Assert.Equal<(string, string)>([("accepted", "Acepto"), ("rejected", "No acepto")], buttons.Items);
        Assert.Equal(new Effect.WaitForCustomer("consent", "Política de datos"), result.Effects[1]);
        Assert.Equal(new ConsentPrompt("consent", ShownToLaura, null), result.State.ConsentPrompt);
    }

    [Fact]
    public void EnteringChecksBeforeAsking()
    {
        var result = Start();

        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Equal(RunStatus.Running, result.State.Status);
        Assert.Equal("consent", result.State.CurrentNodeId);
        Assert.Equal(new TraceEntry(Now, "consent", FlowNodeKinds.DataConsent, TraceOutcomes.Pending), result.State.Trace[^1]);
        Assert.Null(result.State.ConsentPrompt);
    }

    [Fact]
    public void ACurrentAcceptanceLeavesByAcceptedWithoutSending()
    {
        var result = Step(Start().State, new EngineInput.DataConsent("consent", Accepted: true));

        Assert.Equal(new Effect.SendText("yes", "Gracias"), result.Effects[0]);
        Assert.DoesNotContain(result.Effects, effect => effect is Effect.SendInteractive or Effect.RecordDataConsent);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Accepted });
    }

    [Fact]
    public void WithoutAcceptanceItAsksWithTheFixedButtons()
    {
        AssertAsked(Asked(), attempts: 0);
    }

    [Fact]
    public void WhenTheCustomerCannotBeRecordedItLeavesByRejectedWithoutAsking()
    {
        var result = Step(Start().State, new EngineInput.DataConsent("consent", Accepted: false, CanRecord: false));

        Assert.Equal(new Effect.SendText("no", "Entendido"), result.Effects[0]);
        Assert.DoesNotContain(result.Effects, effect => effect is Effect.SendInteractive or Effect.RecordDataConsent);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Rejected });
    }

    [Fact]
    public void TappingAcceptRecordsTheShownTextAndLeaves()
    {
        var waiting = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto enviado", PromptId) };

        var result = Step(waiting, Tapped("accepted", "Acepto"));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto enviado", PromptId), result.Effects[0]);
        Assert.Equal(new Effect.SendText("yes", "Gracias"), result.Effects[1]);
        Assert.Null(result.State.ConsentPrompt);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Accepted });
    }

    [Fact]
    public void TappingRejectRecordsRejected()
    {
        var waiting = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto enviado", PromptId) };

        var result = Step(waiting, Tapped("rejected", "No acepto"));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, "Texto enviado", PromptId), result.Effects[0]);
        Assert.Equal(new Effect.SendText("no", "Entendido"), result.Effects[1]);
        Assert.Null(result.State.ConsentPrompt);
    }

    [Fact]
    public void TypingALabelCountsAsTapping()
    {
        var accepted = Step(Asked().State, Typed("ACEPTO"));
        var rejected = Step(Asked().State, Typed("  no   acepto "));

        Assert.Equal(DataConsentDecision.Accepted, Recorded(accepted).Decision);
        Assert.Equal(new Effect.SendText("yes", "Gracias"), accepted.Effects[1]);
        Assert.Equal(DataConsentDecision.Rejected, Recorded(rejected).Decision);
        Assert.Equal(new Effect.SendText("no", "Entendido"), rejected.Effects[1]);
    }

    [Fact]
    public void TheRecordedTextIsTheOneSentNotTheCurrentTemplate()
    {
        var waiting = Asked().State with { Variables = RunVariables.Parse("""{"nombre":"Pedro"}""") };

        var result = Step(waiting, Tapped("accepted", "Acepto"));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, ShownToLaura, null), result.Effects[0]);
    }

    [Fact]
    public void WithoutARememberedPromptTheTextIsInterpolatedAgain()
    {
        var forgotten = Asked().State with { ConsentPrompt = null };
        var otherBlock = Asked().State with { ConsentPrompt = new ConsentPrompt("otro", "Otro texto", PromptId) };

        var fromNull = Step(forgotten, Tapped("accepted", "Acepto"));
        var fromOther = Step(otherBlock, Tapped("accepted", "Acepto"));

        var expected = new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, ShownToLaura, null);
        Assert.Equal(expected, fromNull.Effects[0]);
        Assert.Equal(expected, fromOther.Effects[0]);
    }

    [Fact]
    public void AnInvalidReplyAsksAgain()
    {
        var invalid = Step(Asked().State, Typed("hola"));

        Assert.Equal(1, invalid.State.Attempts);
        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], invalid.Effects);
        Assert.Equal(RunStatus.Running, invalid.State.Status);
        Assert.Null(invalid.State.ConsentPrompt);

        var askedAgain = Step(invalid.State, new EngineInput.DataConsent("consent", Accepted: false));

        AssertAsked(askedAgain, attempts: 1);
    }

    [Fact]
    public void ExhaustedAttemptsLeaveByRejectedWithoutRecording()
    {
        var settings = new EngineSettings("reiniciar", 3, AttemptsExhaustedAction.Handoff, Member);
        var result = Asked(settings);
        var effects = new List<Effect>();
        for (var reply = 0; reply < 3; reply++)
        {
            result = Step(result.State, Typed("hola"), settings);
            effects.AddRange(result.Effects);
            if (result.State.Status == RunStatus.Running)
            {
                result = Step(result.State, new EngineInput.DataConsent("consent", Accepted: false), settings);
                effects.AddRange(result.Effects);
            }
        }

        Assert.Equal(new Effect.DataConsentUnanswered("consent"), result.Effects[0]);
        Assert.Equal(new Effect.SendText("no", "Entendido"), result.Effects[1]);
        Assert.DoesNotContain(effects, effect => effect is Effect.RecordDataConsent or Effect.Handoff);
        Assert.NotEqual(RunStatus.HandedOff, result.State.Status);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Rejected });
        Assert.Null(result.State.ConsentPrompt);
    }

    [Fact]
    public void TheRestartKeywordWhileWaitingRecordsNothing()
    {
        var result = Step(Asked().State, Typed("reiniciar"));

        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: TraceOutcomes.Restart });
        Assert.Null(result.State.ConsentPrompt);
    }

    /// <summary>Ruling P2-R5: el procesador deriva el clientId de (corrida, StepNumber al empezar la unidad, índice del envío). La respuesta
    /// a la verificación puede llegar en una unidad propia; si preguntar no subiera StepNumber, la unidad siguiente partiría de la misma
    /// base que la pregunta y su reenvío reusaría el clientId, que Mensajería daría por enviado.</summary>
    [Fact]
    public void EachAskInItsOwnUnitStartsFromADifferentBase()
    {
        var pending = Start().State;

        var firstAsk = Step(pending, new EngineInput.DataConsent("consent", Accepted: false));
        var invalid = Step(firstAsk.State, Typed("hola"));
        var secondAsk = Step(invalid.State, new EngineInput.DataConsent("consent", Accepted: false));

        // Las bases de las dos unidades que preguntan, y la de la unidad que sigue a cada una.
        Assert.Equal(pending.StepNumber + 1, firstAsk.State.StepNumber);
        Assert.Equal(invalid.State.StepNumber + 1, secondAsk.State.StepNumber);
        Assert.NotEqual(pending.StepNumber, invalid.State.StepNumber);
        Assert.Equal(1, secondAsk.State.Attempts);
    }

    [Fact]
    public void FailingWhileWaitingForgetsThePrompt()
    {
        var corrupt = Asked().State with { CurrentNodeId = "missing" };

        var result = Step(corrupt, Typed("Acepto"));

        Assert.Equal(RunStatus.Failed, result.State.Status);
        Assert.Null(result.State.ConsentPrompt);
        Assert.DoesNotContain(result.Effects, effect => effect is Effect.RecordDataConsent);
    }

    [Fact]
    public void ACurrentAcceptanceWinsEvenWhenNothingCouldBeRecorded()
    {
        var result = Step(Start().State, new EngineInput.DataConsent("consent", Accepted: true, CanRecord: false));

        Assert.Equal(new Effect.SendText("yes", "Gracias"), result.Effects[0]);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Accepted });
    }

    [Fact]
    public void AStaleConsentResultChangesNothing()
    {
        var pending = Start().State;
        var waiting = Asked().State;

        var otherBlock = Step(pending, new EngineInput.DataConsent("otro", Accepted: true));
        var whileWaiting = Step(waiting, new EngineInput.DataConsent("consent", Accepted: true));

        Assert.Empty(otherBlock.Effects);
        Assert.Same(pending, otherBlock.State);
        Assert.Empty(whileWaiting.Effects);
        Assert.Same(waiting, whileWaiting.State);
    }
}
