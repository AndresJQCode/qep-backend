using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-01 D-4 a D-9: <see cref="ManualRunStarter"/> decide si una pausa manual puede seguir en el paso pendiente, con una
/// sola regla para el resumen y para el POST, y lo confirma bajo el candado de la conversación.</summary>
public sealed partial class RunProcessorTests
{
    private static readonly DateTimeOffset ContinueNow = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset RunStartedAt = ContinueNow.AddHours(-2);
    private static readonly DateTimeOffset PausedAt = ContinueNow.AddHours(-1);

    private static AutomationConversationState PausedByHand(string reason = "Manual", DateTimeOffset? resumedAt = null, DateTimeOffset? lastInboundAt = null) =>
        new(
            "Open", null, "Paused", ChatbotId, ConnectionId, lastInboundAt ?? PausedAt.AddMinutes(-5), "Laura", "573001234567", resumedAt, reason,
            "WhatsApp", false, ContinueNow.AddHours(1), PausedAt);

    private static ConversationRun RunOf(RunStatus status = RunStatus.Waiting, Guid? chatbotId = null) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, chatbotId ?? ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = status, CurrentNodeId = "n1" }, RunStartedAt);

    /// <summary>Lo que no se puede continuar con la conversación pausada y el botón disponible: el modal no promete y el POST rechaza.</summary>
    public static TheoryData<string> NotContinuable =>
    [
        "running-run", "no-run", "finished-run", "handoff", "handoff-with-run", "advisor-took", "advisor-replied", "advisor-transferred", "resumed-after-start",
        "other-chatbot", "taken-after-pause",
    ];

    private static ContinueBed ContinueScenario(string scenario) => scenario switch
    {
        "running-run" => new ContinueBed(PausedByHand(), RunOf(RunStatus.Running)),
        "no-run" => new ContinueBed(PausedByHand()),
        "finished-run" => new ContinueBed(PausedByHand(), Finished()),
        "handoff" => new ContinueBed(PausedByHand("Handoff")),
        "handoff-with-run" => new ContinueBed(PausedByHand("Handoff"), RunOf()),
        "advisor-took" => new ContinueBed(PausedByHand("AdvisorTook"), RunOf()),
        "advisor-replied" => new ContinueBed(PausedByHand("AdvisorReplied"), RunOf()),
        "advisor-transferred" => new ContinueBed(PausedByHand("AdvisorTransferred"), RunOf()),
        "resumed-after-start" => new ContinueBed(PausedByHand(resumedAt: RunStartedAt.AddMinutes(1)), RunOf()),
        "other-chatbot" => new ContinueBed(PausedByHand(), RunOf(chatbotId: Guid.CreateVersion7())),
        "taken-after-pause" => new ContinueBed(PausedByHand(), RunOf(), taken: true),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    private static ConversationRun Finished()
    {
        var run = RunOf();
        run.Finish(RunStatus.Interrupted, null, ContinueNow.AddMinutes(-1));
        return run;
    }

    [Fact]
    public async Task AManualPauseWithAWaitingRunOffersThePendingStep()
    {
        var bed = new ContinueBed(PausedByHand(), RunOf());

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Available, evaluation.Verdict);
        Assert.Equal(new ManualContinueOffer(ManualContinueMode.PendingStep, CustomerWroteDuringPause: false), evaluation.Continue);
    }

    /// <summary>D-4 y D-6: la misma regla en el resumen y en el POST; si no, el modal promete algo que el botón no hace.</summary>
    [Theory]
    [MemberData(nameof(NotContinuable))]
    public async Task TheSummaryAndThePostAgreeOnWhatCannotContinue(string scenario)
    {
        var bed = ContinueScenario(scenario);

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);
        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Available, evaluation.Verdict);
        Assert.Equal(ManualContinueMode.FlowStart, evaluation.Continue?.Mode);
        Assert.Equal(ManualStartVerdict.CannotContinue, verdict);
        Assert.Equal(0, bed.Commits);
        Assert.Equal(0, bed.Unit.Saves);
    }

    /// <summary>Una corrida de arranque manual lleva <c>StartedAt</c> igual al piso de <c>AutomationResumedAt</c>: no es posterior.</summary>
    [Fact]
    public async Task ARunOfAManualStartContinues()
    {
        var bed = new ContinueBed(PausedByHand(resumedAt: RunStartedAt), RunOf());

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);
        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(ManualContinueMode.PendingStep, evaluation.Continue?.Mode);
        Assert.Equal(ManualStartVerdict.Available, verdict);
    }

    /// <summary>D-5: los eventos de un asesor se miden desde la pausa actual.</summary>
    [Fact]
    public async Task TheAdvisorEventsAreAskedFromThePause()
    {
        var bed = new ContinueBed(PausedByHand(), RunOf());

        await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);
        await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal([PausedAt, PausedAt], bed.Conversations.TakenChecks);
    }

    /// <summary>D-5: la consulta corre en cada detalle de una pausa sólo si el resto de D-4 ya dio verdadero.</summary>
    [Theory]
    [InlineData("handoff")]
    [InlineData("no-run")]
    [InlineData("other-chatbot")]
    public async Task TheAdvisorEventsAreNotAskedWhenTheRestAlreadyFailed(string scenario)
    {
        var bed = ContinueScenario(scenario);

        await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);

        Assert.Empty(bed.Conversations.TakenChecks);
    }

    /// <summary>D-6: «la corrida ya se lee ahí»: el resumen no paga una segunda lectura.</summary>
    [Fact]
    public async Task EvaluatingReadsTheRunOnce()
    {
        var bed = new ContinueBed(PausedByHand(), RunOf());

        await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);

        Assert.Equal(1, bed.Runs.LatestReads);
    }

    /// <summary>§5: sin el botón disponible, o sin pausa, no hay modal de dos opciones.</summary>
    [Theory]
    [InlineData("window-closed", ManualStartVerdict.WindowClosed)]
    [InlineData("chatbot-paused", ManualStartVerdict.NoChatbot)]
    [InlineData("advisor", ManualStartVerdict.HasAdvisor)]
    [InlineData("none", ManualStartVerdict.Available)]
    [InlineData("active", ManualStartVerdict.BotAttending)]
    public async Task AnyOtherCaseOffersNoModal(string scenario, ManualStartVerdict expected)
    {
        var bed = scenario switch
        {
            "window-closed" => new ContinueBed(PausedByHand() with { WindowExpiresAt = ContinueNow.AddMinutes(-1) }, RunOf()),
            "chatbot-paused" => new ContinueBed(PausedByHand(), RunOf(), chatbotStatus: ChatbotStatus.Paused),
            "advisor" => new ContinueBed(PausedByHand() with { AssignedMemberId = Guid.CreateVersion7() }, RunOf()),
            "none" => new ContinueBed(PausedByHand() with { AutomationStatus = "None", AutomationPauseReason = null, AutomationPausedAt = null }),
            _ => new ContinueBed(PausedByHand() with { AutomationStatus = "Active", AutomationPauseReason = null, AutomationPausedAt = null }, RunOf()),
        };

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);

        Assert.Equal(expected, evaluation.Verdict);
        Assert.Null(evaluation.Continue);
    }

    /// <summary>D-10: un entrante de antes de la pausa no enciende el aviso, uno de después sí; también con <c>FlowStart</c>, porque el
    /// asistente tampoco lee esos mensajes al empezar de cero.</summary>
    [Theory]
    [InlineData(-5, false, ManualContinueMode.PendingStep)]
    [InlineData(0, false, ManualContinueMode.PendingStep)]
    [InlineData(5, true, ManualContinueMode.PendingStep)]
    [InlineData(5, true, ManualContinueMode.FlowStart)]
    public async Task TheCustomerWritingDuringThePauseIsTold(int inboundOffsetMinutes, bool expected, ManualContinueMode mode)
    {
        var state = PausedByHand(lastInboundAt: PausedAt.AddMinutes(inboundOffsetMinutes));
        var bed = mode == ManualContinueMode.PendingStep ? new ContinueBed(state, RunOf()) : new ContinueBed(state);

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);

        Assert.Equal(new ManualContinueOffer(mode, expected), evaluation.Continue);
    }

    /// <summary>D-8 y D-9: la corrida no se mueve de su paso, sólo renueva el reloj; ningún registro dice que arrancó o avanzó.</summary>
    [Fact]
    public async Task ContinuingCommitsOnceAndOnlyRenewsTheClock()
    {
        var run = RunOf();
        var bed = new ContinueBed(PausedByHand(), run);
        var inProgress = run.ToState();

        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Available, verdict);
        Assert.Equal(1, bed.Commits);
        Assert.Equal(1, bed.Unit.Saves);
        Assert.Equal(bed.Clock.UtcNow, run.UpdatedAt);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(inProgress.CurrentNodeId, run.CurrentNodeId);
        Assert.Equal(inProgress.StepNumber, run.StepNumber);
        Assert.Empty(bed.Units.Entries);
    }

    [Fact]
    public async Task ABusyLockCannotContinueAndTouchesNothing()
    {
        var run = RunOf();
        var bed = new ContinueBed(PausedByHand(), run, lockBusy: true);

        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Busy, verdict);
        Assert.Equal(0, bed.Commits);
        Assert.Equal(RunStartedAt, run.UpdatedAt);
    }

    /// <summary>D-12: la versión de la corrida se lee bajo el candado; el resumen no la lee para no cargar el flujo en cada detalle.</summary>
    [Fact]
    public async Task AnUnreadableVersionCannotContinueAndIsNotReadByTheSummary()
    {
        var run = RunOf();
        var bed = new ContinueBed(PausedByHand(), run, flow: "esto no es un flujo");

        var evaluation = await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);
        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(ManualContinueMode.PendingStep, evaluation.Continue?.Mode);
        Assert.Equal(ManualStartVerdict.CannotContinue, verdict);
        Assert.Equal(0, bed.Commits);
        Assert.Equal(RunStartedAt, run.UpdatedAt);
    }

    /// <summary>§7: lo que ya existía para iniciar sigue saliendo con su veredicto y sin confirmar nada.</summary>
    [Theory]
    [InlineData("chatbot-paused", ManualStartVerdict.NoChatbot)]
    [InlineData("window-closed", ManualStartVerdict.WindowClosed)]
    [InlineData("active", ManualStartVerdict.BotAttending)]
    public async Task ContinuingKeepsTheVerdictsOfTheStart(string scenario, ManualStartVerdict expected)
    {
        var bed = scenario switch
        {
            "chatbot-paused" => new ContinueBed(PausedByHand(), RunOf(), chatbotStatus: ChatbotStatus.Paused),
            "window-closed" => new ContinueBed(PausedByHand() with { WindowExpiresAt = ContinueNow.AddMinutes(-1) }, RunOf()),
            _ => new ContinueBed(PausedByHand() with { AutomationStatus = "Active", AutomationPauseReason = null, AutomationPausedAt = null }, RunOf()),
        };

        var verdict = await bed.Starter().ContinueAsync(TenantId, ConversationId, bed.Commit, TestContext.Current.CancellationToken);

        Assert.Equal(expected, verdict);
        Assert.Equal(0, bed.Commits);
    }

    /// <summary>Un 412 de Mensajería no renueva el reloj: nada se guardó.</summary>
    [Fact]
    public async Task AFailedCommitSavesNothingOfTheRun()
    {
        var bed = new ContinueBed(PausedByHand(), RunOf());

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.Starter().ContinueAsync(
            TenantId, ConversationId, _ => Task.FromException(new RequestConcurrencyException("concurrency.conflict", "La conversación cambió.")),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, bed.Unit.Saves);
    }

    /// <summary>D-8: el barrido eligió la corrida antes del continuar y llega después: ve el <c>UpdatedAt</c> nuevo y no la toca.</summary>
    [Fact]
    public async Task ACandidatePickedBeforeContinuingIsNotAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(startFailure: null, ActiveState());
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);
        run.KeepWaiting(now.AddMinutes(-1));

        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
    }

    /// <summary>D-9: continuada a la hora 23, sigue viva a la hora 24 y a las 24 h del clic termina <c>Abandoned</c>.</summary>
    [Fact]
    public async Task TheAbandonClockCountsFromTheContinue()
    {
        var start = DateTimeOffset.UtcNow;
        var clicked = start.AddHours(23);
        var bed = new Bed(startFailure: null, ActiveState());
        var run = WaitingRun(start);
        bed.Runs.Added.Add(run);
        run.KeepWaiting(clicked);

        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), start.AddHours(24), TestContext.Current.CancellationToken));
        Assert.Equal(RunStatus.Waiting, run.Status);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), clicked.AddHours(24).AddMinutes(1), TestContext.Current.CancellationToken));
        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.Equal(1, bed.Conversations.Ended);
    }

    /// <summary>§1: pausada, el barrido la cierra <c>Interrupted</c> y la conversación se queda como está; por eso el resumen pasa a
    /// <c>FlowStart</c> con el modal abierto o no.</summary>
    [Fact]
    public async Task APausedRunSweptAtTwentyFourHoursMovesTheSummaryToFlowStart()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(startFailure: null, PausedByHand());
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
        var evaluation = await new ContinueBed(PausedByHand(), run).Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken);
        Assert.Equal(ManualContinueMode.FlowStart, evaluation.Continue?.Mode);
    }

    private sealed class ContinueBed
    {
        private readonly bool lockBusy;
        private readonly string? flow;
        private readonly ChatbotStatus chatbotStatus;

        public ContinueBed(
            AutomationConversationState state, ConversationRun? run = null, bool taken = false, string? flow = Flow,
            ChatbotStatus chatbotStatus = ChatbotStatus.Active, bool lockBusy = false)
        {
            Conversations = new ManualConversations(state, taken);
            this.flow = flow;
            this.chatbotStatus = chatbotStatus;
            this.lockBusy = lockBusy;
            if (run is not null)
            {
                Runs.Added.Add(run);
            }
        }

        public NoUnitOfWork Unit { get; } = new(null, false);

        public MemoryRuns Runs { get; } = new();

        public ManualRunUnits Units { get; } = new();

        public ManualConversations Conversations { get; }

        public FixedClock Clock { get; } = new();

        public int Commits { get; private set; }

        public Task Commit(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.CompletedTask;
        }

        public ManualRunStarter Starter() => new(
            lockBusy ? new NoUnitOfWork(null, true) : Unit, Runs, new ServingChatbots(flow, status: chatbotStatus), new RecordingInbox(), Conversations,
            new ModuleStatus(true), new RunUnitWriter(Units, Clock, NullLogger<RunUnitWriter>.Instance), Clock);
    }
}
