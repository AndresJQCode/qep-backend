using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-26 D-2, D-4, D-6 y D-7: el bloque Política de datos en el motor.</summary>
public sealed class FlowEngineDataConsentTests
{
    private const string ConsentText = "Hola {nombre_contacto}, lee https://e.co/d";
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

    private static RunVariables Laura() => RunVariables.Parse("""{"nombre_contacto":"Laura"}""");

    private static StepResult Step(RunState state, EngineInput input, EngineSettings? settings = null) =>
        FlowEngine.Step(Flow, settings ?? Settings, state, input, Now);

    private static StepResult Start(EngineSettings? settings = null) => Step(RunState.Initial(Laura()), new EngineInput.Start(), settings);

    /// <summary>La corrida con la pregunta ya enviada: el cliente no había aceptado.</summary>
    private static StepResult Asked(EngineSettings? settings = null) =>
        Step(Start(settings).State, new EngineInput.DataConsent("consent", Accepted: false), settings);

    private static EngineInput.CustomerMessage Typed(string text) => new("Text", text, null, null);

    private static EngineInput.CustomerMessage Tapped(string id, string title, Guid? quoting = null) =>
        new("Interactive", title, id, null, quoting);

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
        var waiting = Asked().State with { Variables = RunVariables.Parse("""{"nombre_contacto":"Pedro"}""") };

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

    /// <summary>Final review F1 (D-5, D-6): los ids de los botones son fijos y WhatsApp deja tocar los viejos. Un toque que cita una
    /// pregunta anterior no es respuesta al texto de la vigente: se pregunta otra vez, cuenta como intento y no se registra nada.</summary>
    [Fact]
    public void ATapQuotingAnOlderPromptAsksAgain()
    {
        var waiting = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto vigente", PromptId) };

        var result = Step(waiting, Tapped("accepted", "Acepto", quoting: Guid.CreateVersion7()));

        Assert.DoesNotContain(result.Effects, effect => effect is Effect.RecordDataConsent);
        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Equal(1, result.State.Attempts);
        Assert.Equal("consent", result.State.CurrentNodeId);
    }

    [Fact]
    public void ATapQuotingTheCurrentPromptRecords()
    {
        var waiting = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto vigente", PromptId) };

        var result = Step(waiting, Tapped("accepted", "Acepto", quoting: PromptId));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto vigente", PromptId), result.Effects[0]);
    }

    /// <summary>Sólo un toque se ata a su mensaje: lo escrito no tiene botón viejo que tocar, aunque cite otro mensaje.</summary>
    [Fact]
    public void ATypedLabelRecordsEvenQuotingAnotherMessage()
    {
        var waiting = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto vigente", PromptId) };

        var result = Step(waiting, new EngineInput.CustomerMessage("Text", "Acepto", null, null, Guid.CreateVersion7()));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto vigente", PromptId), result.Effects[0]);
    }

    /// <summary>Sin saber qué mensaje cita el toque, o sin saber cuál fue la pregunta, no hay con qué compararlo: vale como hoy.</summary>
    [Fact]
    public void WithoutBothMessagesKnownATapRecords()
    {
        var known = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto vigente", PromptId) };
        var unknownPrompt = Asked().State with { ConsentPrompt = new ConsentPrompt("consent", "Texto vigente", null) };

        var unquoted = Step(known, Tapped("accepted", "Acepto"));
        var promptUnknown = Step(unknownPrompt, Tapped("accepted", "Acepto", quoting: Guid.CreateVersion7()));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto vigente", PromptId), unquoted.Effects[0]);
        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto vigente", null), promptUnknown.Effects[0]);
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

    // Spec 2026-10-26 repetir R-3, R-4 y R-4b: «Si no acepta» en el motor.

    private static readonly Guid OtherMember = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid FirstPrompt = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SecondPrompt = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static FlowDefinition FlowWith(DataConsentNode consent) => new(
        FlowDefinition.CurrentSchema, new FlowStart("consent"), [],
        [
            consent,
            new MessageNode("yes", "Mensaje yes", Out((FlowOutputs.Next, null)), "Gracias", null),
            new MessageNode("no", "Mensaje no", Out((FlowOutputs.Next, null)), "Entendido", null),
        ]);

    private static FlowDefinition Repeating(int maxAsks, Guid? member = null) => FlowWith(new DataConsentNode(
        "consent", "Política de datos", Out((FlowOutputs.Accepted, "yes")), ConsentText, DataConsentRejectActions.Repeat, maxAsks, member));

    private static FlowDefinition HandingOff(Guid? member = null) => FlowWith(new DataConsentNode(
        "consent", "Política de datos", Out((FlowOutputs.Accepted, "yes")), ConsentText, DataConsentRejectActions.Handoff,
        OnRejectMemberId: member));

    private static StepResult Step(FlowDefinition flow, RunState state, EngineInput input, EngineSettings? settings = null) =>
        FlowEngine.Step(flow, settings ?? Settings, state, input, Now);

    private static StepResult Asked(FlowDefinition flow, EngineSettings? settings = null)
    {
        var pending = Step(flow, RunState.Initial(Laura()), new EngineInput.Start(), settings);
        return Step(flow, pending.State, new EngineInput.DataConsent("consent", Accepted: false), settings);
    }

    private static EngineInput.CustomerMessage NotAccepted() => Tapped(FlowOutputs.Rejected, DataConsentButtons.RejectLabel);

    private static void AssertButtons(Effect effect)
    {
        var send = Assert.IsType<Effect.SendInteractive>(effect);
        Assert.Equal("consent", send.NodeId);
        var buttons = Assert.IsType<InteractiveMessage.Buttons>(send.Message);
        Assert.Equal(ShownToLaura, buttons.Body);
        Assert.Equal<(string, string)>([("accepted", "Acepto"), ("rejected", "No acepto")], buttons.Items);
    }

    private static void AssertHandedOff(StepResult result)
    {
        Assert.Equal(RunStatus.HandedOff, result.State.Status);
        Assert.Equal(new TraceEntry(Now, "consent", FlowNodeKinds.DataConsent, TraceOutcomes.HandedOff), result.State.Trace[^1]);
        Assert.Null(result.State.ConsentPrompt);
    }

    /// <summary>Respuestas inválidas hasta agotar los intentos, volviendo a preguntar entre una y otra como hace el procesador.</summary>
    private static (StepResult Result, List<Effect> Effects) ExhaustAttempts(FlowDefinition flow, EngineSettings settings)
    {
        var result = Asked(flow, settings);
        var effects = new List<Effect>();
        for (var reply = 0; reply < settings.MaxAttempts; reply++)
        {
            result = Step(flow, result.State, Typed("hola"), settings);
            effects.AddRange(result.Effects);
            if (result.State.Status == RunStatus.Running)
            {
                result = Step(flow, result.State, new EngineInput.DataConsent("consent", Accepted: false), settings);
                effects.AddRange(result.Effects);
            }
        }

        return (result, effects);
    }

    [Fact]
    public void RepeatingAsksAgainAfterARejectionAndRecordsIt()
    {
        var flow = Repeating(maxAsks: 3);
        var waiting = Asked(flow).State with { ConsentPrompt = new ConsentPrompt("consent", "Texto enviado", PromptId) };

        var result = Step(flow, waiting, NotAccepted());

        Assert.Equal(3, result.Effects.Count);
        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, "Texto enviado", PromptId), result.Effects[0]);
        AssertButtons(result.Effects[1]);
        Assert.Equal(new Effect.WaitForCustomer("consent", "Política de datos"), result.Effects[2]);
        Assert.Equal(RunStatus.Waiting, result.State.Status);
        Assert.Equal("consent", result.State.CurrentNodeId);
        Assert.Equal(1, result.State.ConsentRejections);
        Assert.Equal(new ConsentPrompt("consent", ShownToLaura, null), result.State.ConsentPrompt);
        Assert.Equal(waiting.StepNumber + 1, result.State.StepNumber);
        Assert.DoesNotContain(result.State.Trace, entry => entry.Outcome is FlowOutputs.Rejected or FlowOutputs.Accepted);
    }

    [Fact]
    public void ReachingTheCapHandsOffWithTheBlockDestination()
    {
        var flow = Repeating(maxAsks: 3, Member);
        var result = Asked(flow);
        var rounds = new List<StepResult>();
        for (var rejection = 0; rejection < 3; rejection++)
        {
            result = Step(flow, result.State, NotAccepted());
            rounds.Add(result);
        }

        Assert.All(rounds, round => Assert.Equal(DataConsentDecision.Rejected, Recorded(round).Decision));
        AssertButtons(rounds[0].Effects[1]);
        AssertButtons(rounds[1].Effects[1]);
        Assert.Equal<Effect>(
            [
                new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, ShownToLaura, null),
                new Effect.Handoff("consent", "data_consent_rejected", Member),
            ],
            result.Effects);
        AssertHandedOff(result);
        Assert.Equal(3, result.State.ConsentRejections);
        Assert.Equal("data_consent_rejected", HandoffReasons.DataConsentRejected);
    }

    [Fact]
    public void ACapOfOneHandsOffOnTheFirstRejection()
    {
        var flow = Repeating(maxAsks: 1);

        var result = Step(flow, Asked(flow).State, NotAccepted());

        Assert.Equal<Effect>(
            [
                new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, ShownToLaura, null),
                new Effect.Handoff("consent", "data_consent_rejected", null),
            ],
            result.Effects);
        AssertHandedOff(result);
    }

    [Fact]
    public void HandingOffModeHandsOffOnTheFirstRejection()
    {
        var flow = HandingOff(Member);

        var result = Step(flow, Asked(flow).State, NotAccepted());

        Assert.Equal<Effect>(
            [
                new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, ShownToLaura, null),
                new Effect.Handoff("consent", "data_consent_rejected", Member),
            ],
            result.Effects);
        AssertHandedOff(result);
        Assert.Equal(0, result.State.ConsentRejections);
    }

    /// <summary>R-4: las respuestas inválidas no cuentan para el tope de «No acepto», ni al revés.</summary>
    [Fact]
    public void InvalidRepliesAndRejectionsCountApart()
    {
        var flow = Repeating(maxAsks: 2);
        var settings = new EngineSettings("reiniciar", 3);

        var firstInvalid = Step(flow, Asked(flow, settings).State, Typed("hola"), settings);
        Assert.Equal(1, firstInvalid.State.Attempts);
        Assert.Equal(0, firstInvalid.State.ConsentRejections);

        var askedAgain = Step(flow, firstInvalid.State, new EngineInput.DataConsent("consent", Accepted: false), settings);
        var firstRejection = Step(flow, askedAgain.State, NotAccepted(), settings);
        Assert.Equal(1, firstRejection.State.Attempts);
        Assert.Equal(1, firstRejection.State.ConsentRejections);
        Assert.Equal(RunStatus.Waiting, firstRejection.State.Status);
        AssertButtons(firstRejection.Effects[1]);

        var secondInvalid = Step(flow, firstRejection.State, Typed("hola"), settings);
        Assert.Equal(2, secondInvalid.State.Attempts);
        Assert.Equal(1, secondInvalid.State.ConsentRejections);

        var askedOnceMore = Step(flow, secondInvalid.State, new EngineInput.DataConsent("consent", Accepted: false), settings);
        var secondRejection = Step(flow, askedOnceMore.State, NotAccepted(), settings);
        Assert.Equal(new Effect.Handoff("consent", "data_consent_rejected", null), secondRejection.Effects[^1]);
        AssertHandedOff(secondRejection);
    }

    /// <summary>R-4: en «repeat» y «handoff» no hay «rejected»; al agotar los intentos pasa a la bandeja con el destino del bloque, sin
    /// registrar nada, y la regla de agotamiento del chatbot no se lee.</summary>
    [Fact]
    public void ExhaustedAttemptsHandOffWithTheBlockDestination()
    {
        FlowDefinition[] flows = [Repeating(maxAsks: 3, Member), HandingOff(Member)];
        EngineSettings[] rules =
        [
            new("reiniciar", 3, AttemptsExhaustedAction.Restart),
            new("reiniciar", 3, AttemptsExhaustedAction.Handoff, OtherMember),
        ];

        foreach (var flow in flows)
        {
            foreach (var settings in rules)
            {
                var (result, effects) = ExhaustAttempts(flow, settings);

                Assert.Equal<Effect>(
                    [new Effect.DataConsentUnanswered("consent"), new Effect.Handoff("consent", "attempts_exhausted", Member)],
                    result.Effects);
                Assert.DoesNotContain(effects, effect => effect is Effect.RecordDataConsent);
                Assert.DoesNotContain(result.State.Trace, entry => entry.Outcome == TraceOutcomes.Restart);
                AssertHandedOff(result);
            }
        }
    }

    [Fact]
    public void ExhaustedAttemptsInRejectedOutputLeaveAsToday()
    {
        var flow = FlowWith(new DataConsentNode(
            "consent", "Política de datos", Out((FlowOutputs.Accepted, "yes"), (FlowOutputs.Rejected, "no")), ConsentText,
            DataConsentRejectActions.RejectedOutput, OnRejectMemberId: Member));
        var settings = new EngineSettings("reiniciar", 3, AttemptsExhaustedAction.Handoff, OtherMember);

        var (result, effects) = ExhaustAttempts(flow, settings);

        Assert.Equal(new Effect.DataConsentUnanswered("consent"), result.Effects[0]);
        Assert.Equal(new Effect.SendText("no", "Entendido"), result.Effects[1]);
        Assert.DoesNotContain(effects, effect => effect is Effect.RecordDataConsent or Effect.Handoff);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Rejected });
    }

    /// <summary>R-4b: sin «rejected» salir por ella terminaría la corrida con el bot callado; pasa a la bandeja con el destino del bloque.</summary>
    [Fact]
    public void AnUnrecordableCustomerHandsOffWithoutAsking()
    {
        foreach (var flow in new[] { Repeating(maxAsks: 3, Member), HandingOff(Member) })
        {
            var pending = Step(flow, RunState.Initial(Laura()), new EngineInput.Start());

            var result = Step(flow, pending.State, new EngineInput.DataConsent("consent", Accepted: false, CanRecord: false));

            Assert.Equal<Effect>([new Effect.Handoff("consent", "data_consent_unavailable", Member)], result.Effects);
            AssertHandedOff(result);
        }

        Assert.Equal("data_consent_unavailable", HandoffReasons.DataConsentUnavailable);

        var today = Step(Start().State, new EngineInput.DataConsent("consent", Accepted: false, CanRecord: false));
        Assert.Equal<Effect>([new Effect.SendText("no", "Entendido"), new Effect.EndRun("no", EndReasons.OutputUnconnected)], today.Effects);
    }

    [Fact]
    public void ACurrentAcceptanceStillLeavesByAccepted()
    {
        foreach (var flow in new[] { Repeating(maxAsks: 3), HandingOff() })
        {
            var pending = Step(flow, RunState.Initial(Laura()), new EngineInput.Start());

            var result = Step(flow, pending.State, new EngineInput.DataConsent("consent", Accepted: true));

            Assert.Equal(new Effect.SendText("yes", "Gracias"), result.Effects[0]);
            Assert.DoesNotContain(result.Effects, effect => effect is Effect.SendInteractive or Effect.RecordDataConsent or Effect.Handoff);
            Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Accepted });
        }
    }

    /// <summary>Contrato: un bloque publicado o sin normalizar puede traer «rejected» conectada con «repeat»; nadie la sigue.</summary>
    [Fact]
    public void ARepeatBlockNeverFollowsAHiddenRejected()
    {
        var flow = FlowWith(new DataConsentNode(
            "consent", "Política de datos", Out((FlowOutputs.Accepted, "yes"), (FlowOutputs.Rejected, "no")), ConsentText,
            DataConsentRejectActions.Repeat, 3));
        var result = Asked(flow);
        var effects = new List<Effect>();
        for (var rejection = 0; rejection < 3; rejection++)
        {
            result = Step(flow, result.State, NotAccepted());
            effects.AddRange(result.Effects);
        }

        Assert.DoesNotContain(effects, effect => effect is Effect.SendText { NodeId: "no" });
        Assert.DoesNotContain(result.State.Trace, entry => entry.NodeId == "no" || entry.Outcome == FlowOutputs.Rejected);
        Assert.Equal(3, effects.Count(effect => effect is Effect.RecordDataConsent));
        Assert.Equal(new Effect.Handoff("consent", "data_consent_rejected", null), result.Effects[^1]);
    }

    /// <summary>R-1: un bloque guardado antes del modo es «rejectedOutput» y sale por «rejected» como antes.</summary>
    [Fact]
    public void AnOldBlockLeavesByRejectedAsBefore()
    {
        var old = new DataConsentNode(
            "consent", "Política de datos", Out((FlowOutputs.Accepted, "yes"), (FlowOutputs.Rejected, "no")), ConsentText);
        var flow = FlowWith(old);

        var result = Step(flow, Asked(flow).State, NotAccepted());

        Assert.Equal(DataConsentRejectActions.RejectedOutput, old.OnReject);
        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Rejected, ShownToLaura, null), result.Effects[0]);
        Assert.Equal(new Effect.SendText("no", "Entendido"), result.Effects[1]);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Rejected });
        Assert.Equal(0, result.State.ConsentRejections);
    }

    /// <summary>Final review F1 con el modo: tras repetir, el «No acepto» de la pregunta anterior cita otro mensaje; no es una respuesta
    /// a la vigente, así que no registra ni suma al tope, y cuenta como intento.</summary>
    [Fact]
    public void ATapOnThePreviousPromptAfterARepeatIsNotARejection()
    {
        var flow = Repeating(maxAsks: 3);
        var waiting = Asked(flow).State with
        {
            ConsentPrompt = new ConsentPrompt("consent", ShownToLaura, SecondPrompt),
            ConsentRejections = 1,
        };

        var result = Step(flow, waiting, Tapped(FlowOutputs.Rejected, "No acepto", quoting: FirstPrompt));

        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Equal(1, result.State.Attempts);
        Assert.Equal(1, result.State.ConsentRejections);
    }

    [Fact]
    public void TheRestartKeywordStartsTheCountAgain()
    {
        var flow = Repeating(maxAsks: 3);
        var waiting = Asked(flow).State with { ConsentRejections = 2 };

        var result = Step(flow, waiting, Typed("reiniciar"));

        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Equal(0, result.State.ConsentRejections);
    }

    [Fact]
    public void EnteringTheBlockFromAnotherStartsTheCountAgain()
    {
        var flow = new FlowDefinition(
            FlowDefinition.CurrentSchema, new FlowStart("pick"), [],
            [
                new ButtonsNode(
                    "pick", "Elegir", Out(("go", "consent")), "¿Seguimos?", [new FlowButton("go", "Seguir")], null, InteractiveFallbacks.Repeat),
                .. Repeating(maxAsks: 3).Nodes,
            ]);
        var waiting = Step(flow, RunState.Initial(Laura()), new EngineInput.Start()).State with { ConsentRejections = 2 };
        Assert.Equal("pick", waiting.CurrentNodeId);

        var result = Step(flow, waiting, Tapped("go", "Seguir"));

        Assert.Equal<Effect>([new Effect.CheckDataConsent("consent")], result.Effects);
        Assert.Equal("consent", result.State.CurrentNodeId);
        Assert.Equal(0, result.State.ConsentRejections);
    }

    /// <summary>Fija que aceptar después de rechazar sale por <c>accepted</c> y deja el contador en 0 (pasó a la primera).</summary>
    [Fact]
    public void AnAcceptanceAfterRejectionsLeavesByAcceptedAndResetsTheCount()
    {
        var flow = Repeating(maxAsks: 3);
        var waiting = Asked(flow).State with
        {
            ConsentRejections = 1,
            ConsentPrompt = new ConsentPrompt("consent", "Texto enviado", PromptId),
        };

        var result = Step(flow, waiting, Tapped(FlowOutputs.Accepted, "Acepto"));

        Assert.Equal(new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto enviado", PromptId), result.Effects[0]);
        Assert.Contains(result.State.Trace, entry => entry is { NodeId: "consent", Outcome: FlowOutputs.Accepted });
        Assert.Equal(0, result.State.ConsentRejections);
        Assert.Null(result.State.ConsentPrompt);
    }
}
