using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-31 D-6 y D-7: <see cref="ManualRunStarter"/> decide si una persona puede iniciar el asistente desde el hilo y, al
/// confirmarlo, cierra la corrida muerta y deja que Mensajería confirme el cambio, todo bajo el candado de la conversación.</summary>
public sealed partial class RunProcessorTests
{
    private static readonly DateTimeOffset ManualNow = DateTimeOffset.UtcNow;

    /// <summary>Una fila por veredicto, en el orden en que se deciden. <c>Busy</c> sólo existe al iniciar: evaluar no toma el candado.</summary>
    public static TheoryData<ManualStartVerdict> Verdicts => new()
    {
        ManualStartVerdict.Available,
        ManualStartVerdict.NotFound,
        ManualStartVerdict.NotOpen,
        ManualStartVerdict.HasAdvisor,
        ManualStartVerdict.Blocked,
        ManualStartVerdict.UnsupportedChannel,
        ManualStartVerdict.ModuleDisabled,
        ManualStartVerdict.BotAttending,
        ManualStartVerdict.StartPending,
        ManualStartVerdict.NoChatbot,
        ManualStartVerdict.WindowClosed,
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public async Task EvaluateAndStartGiveTheSameVerdict(ManualStartVerdict verdict)
    {
        var bed = ManualBed.For(verdict);
        var committed = 0;

        var evaluated = (await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken)).Verdict;
        var started = await bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) =>
            {
                committed++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(verdict, evaluated);
        Assert.Equal(verdict, started);
        Assert.Equal(verdict == ManualStartVerdict.Available ? 1 : 0, committed);
    }

    [Fact]
    public async Task ABusyLockIsBusyAndCommitsNothing()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available, lockBusy: true);
        var committed = false;

        var verdict = await bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Busy, verdict);
        Assert.False(committed);
        Assert.Equal(0, bed.Unit.Saves);
    }

    /// <summary>D-7: una corrida viva que nadie interrumpió es el bot atendiendo; no se toca.</summary>
    [Fact]
    public async Task ABotAttendingRejectsAndLeavesItsRunAlive()
    {
        var bed = ManualBed.For(ManualStartVerdict.BotAttending);

        var verdict = await bed.Starter().StartAsync(TenantId, ConversationId, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.BotAttending, verdict);
        Assert.Equal(RunStatus.Waiting, bed.Runs.Added.Single().Status);
        Assert.Equal(0, bed.Unit.Saves);
        Assert.Empty(bed.Units.Entries);
    }

    /// <summary>La corrida muerta (una persona pausó el asistente) se cierra antes de que Mensajería cambie la conversación: el veredicto
    /// sale del estado de antes. No es un mismo guardado: Mensajería confirma primero y, si el de Automations falla, el worker cierra la
    /// corrida por <c>AutomationResumedAt</c>.</summary>
    [Fact]
    public async Task ADeadRunIsClosedBeforeTheConversationChanges()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available, deadRun: true);
        var run = bed.Runs.Added.Single();
        RunStatus? statusInsideCommit = null;

        var verdict = await bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) =>
            {
                statusInsideCommit = run.Status;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Available, verdict);
        Assert.Equal(RunStatus.Interrupted, statusInsideCommit);
        var entry = Assert.Single(bed.Units.Entries);
        Assert.Equal(RunUnitTriggers.Closed, entry.Trigger);
        Assert.Equal(RunCloseReasons.HumanTookOver, entry.ClosedReason);
        Assert.Equal(1, bed.Unit.Saves);
    }

    [Fact]
    public async Task AFailedCommitSavesNothing()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available, deadRun: true);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) => Task.FromException(new RequestConcurrencyException("concurrency.conflict", "La conversación cambió.")),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, bed.Unit.Saves);
    }

    /// <summary>La versión publicada se lee dentro del candado: si ya no se puede leer, no hay chatbot que iniciar.</summary>
    [Fact]
    public async Task AnUnreadableFlowIsNoChatbotAtPress()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available, flow: "esto no es un flujo");
        var committed = false;

        var verdict = await bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.NoChatbot, verdict);
        Assert.False(committed);
    }

    /// <summary>D-6: la última corrida terminó. Una corrida cerrada no es el bot atendiendo ni impide arrancar otra.</summary>
    [Fact]
    public async Task ALatestCompletedRunDoesNotBlockTheStart()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available);
        var completed = WaitingRun(ManualNow.AddHours(-2));
        completed.Finish(RunStatus.Completed, null, ManualNow.AddHours(-1));
        await bed.Runs.AddAsync(completed, TestContext.Current.CancellationToken);
        var committed = false;

        var verdict = await bed.Starter().StartAsync(
            TenantId, ConversationId, (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManualStartVerdict.Available, verdict);
        Assert.True(committed);
        Assert.Empty(bed.Units.Entries);
    }

    /// <summary>Los veredictos que sólo dependen de la conversación se deciden sin leer <c>conversation_runs</c>: el resumen de cada detalle
    /// los evalúa y una conversación resuelta o con asesor no necesita esa lectura.</summary>
    [Theory]
    [InlineData(ManualStartVerdict.NotFound)]
    [InlineData(ManualStartVerdict.NotOpen)]
    [InlineData(ManualStartVerdict.HasAdvisor)]
    [InlineData(ManualStartVerdict.Blocked)]
    [InlineData(ManualStartVerdict.UnsupportedChannel)]
    public async Task EvaluatingAConversationOnlyVerdictReadsNoRun(ManualStartVerdict verdict)
    {
        var bed = ManualBed.For(verdict, deadRun: true);

        var evaluated = (await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken)).Verdict;

        Assert.Equal(verdict, evaluated);
        Assert.Equal(0, bed.Runs.LatestReads);
        Assert.Equal(0, bed.Runs.Reads);
    }

    /// <summary>D-6: una corrida que figura viva pero que una persona interrumpió (pausa manual) no es el bot atendiendo.</summary>
    [Fact]
    public async Task EvaluatingADeadRunIsAvailable()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available, deadRun: true);

        var evaluated = (await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken)).Verdict;

        Assert.Equal(ManualStartVerdict.Available, evaluated);
        Assert.Equal(1, bed.Runs.LatestReads);
    }

    [Fact]
    public async Task EvaluatingALatestCompletedRunIsAvailable()
    {
        var bed = ManualBed.For(ManualStartVerdict.Available);
        var completed = WaitingRun(ManualNow.AddHours(-2));
        completed.Finish(RunStatus.Completed, null, ManualNow.AddHours(-1));
        await bed.Runs.AddAsync(completed, TestContext.Current.CancellationToken);

        var evaluated = (await bed.Starter().EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken)).Verdict;

        Assert.Equal(ManualStartVerdict.Available, evaluated);
    }

    /// <summary>La fila de arranque manual: sin mensaje, así que <c>ReadAsync</c> responde <c>null</c> para ella.</summary>
    private static InboundQueueItem ManualRow(RecordingInbox inbox)
    {
        var row = new InboundQueueItem(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: false, ManualNow, Attempts: 1);
        inbox.NotInbound.Add(row.MessageId);
        return row;
    }

    /// <summary>Review Focus 5: una excepción no prevista no reintenta la fila manual; el lease llegaría a unos 30 minutos con el bot
    /// activo y mudo. Traspasa y cierra.</summary>
    [Fact]
    public async Task AManualStartThatThrowsHandsOffInsteadOfRetrying()
    {
        var bed = new Bed(null, ActiveState());
        bed.Messenger.Failure = new InvalidOperationException("Meta no respondió.");
        var row = ManualRow(bed.Inbox);

        var outcome = await bed.Processor().ProcessAsync(row, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.RunFailed, outcome);
        var handoff = Assert.Single(bed.Conversations.Handoffs);
        Assert.Equal(HandoffCause.FlowFailed(AutomationsRunFailures.ManualStartFailed, null, null), handoff.Cause);
        Assert.Null(handoff.Member);
        Assert.Equal([$"complete {row.MessageId} 1 run_failed"], bed.Inbox.Calls);
    }

    /// <summary>Spec 2026-10-31 D-3: tras el traspaso la conversación queda Paused Handoff, sin corrida viva ni fila pendiente, y el botón
    /// se vuelve a ofrecer a propósito: la persona ve la falla y puede iniciar de nuevo.</summary>
    [Fact]
    public async Task AfterTheFailedStartHandoffTheButtonIsOfferedAgain()
    {
        var bed = new Bed(null, ActiveState());
        bed.Messenger.Failure = new InvalidOperationException("Meta no respondió.");
        await bed.Processor().ProcessAsync(ManualRow(bed.Inbox), TestContext.Current.CancellationToken);
        Assert.Single(bed.Conversations.Handoffs);
        var pausedByHandoff = new AutomationConversationState(
            "Open", null, "Paused", ChatbotId, ConnectionId, ManualNow, "Laura", "573001234567", null, "Handoff", "WhatsApp", false, ManualNow.AddHours(1));
        var starter = new ManualRunStarter(
            new NoUnitOfWork(null, false), new MemoryRuns(), new ServingChatbots(Flow), new RecordingInbox(), new ManualConversations(pausedByHandoff),
            new ModuleStatus(true), new RunUnitWriter(new ManualRunUnits(), new FixedClock(), NullLogger<RunUnitWriter>.Instance), new FixedClock());

        var verdict = (await starter.EvaluateAsync(TenantId, ConversationId, TestContext.Current.CancellationToken)).Verdict;

        Assert.Equal(ManualStartVerdict.Available, verdict);
    }

    /// <summary>Un choque de concurrencia sigue su reintento de 2 s también en la fila manual: no es una falla del arranque.</summary>
    [Fact]
    public async Task AManualRowThatHitsAConcurrencyConflictRetriesWithoutHandingOff()
    {
        var bed = new Bed(null, ActiveState());
        bed.Messenger.Failure = new RequestConcurrencyException("concurrency.conflict", "La conversación cambió.");
        var row = ManualRow(bed.Inbox);

        var outcome = await bed.Processor().ProcessAsync(row, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, outcome);
        Assert.Equal([$"retry {row.MessageId} 1 00:00:02"], bed.Inbox.Calls);
        Assert.Empty(bed.Conversations.Handoffs);
    }

    /// <summary>Con el token del worker cancelado (el servicio se apaga) la cancelación sube: no se traspasa ni se cierra la fila.</summary>
    [Fact]
    public async Task AManualRowWithTheWorkerTokenCancelledPropagatesWithoutHandingOff()
    {
        var bed = new Bed(null, ActiveState());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        bed.Messenger.Failure = new OperationCanceledException(cts.Token);
        var row = ManualRow(bed.Inbox);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bed.Processor().ProcessAsync(row, cts.Token));

        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Empty(bed.Inbox.Calls);
    }

    [Fact]
    public async Task AnInboundThatThrowsKeepsItsLeaseRetry()
    {
        var bed = new Bed(null, ActiveState());
        bed.Messenger.Failure = new InvalidOperationException("Meta no respondió.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken));

        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Empty(bed.Inbox.Calls);
    }

    [Fact]
    public async Task AHandoffThatNoLongerAppliesStillClosesTheRow()
    {
        var bed = new Bed(null, ActiveState());
        bed.Messenger.Failure = new InvalidOperationException("Meta no respondió.");
        bed.Conversations.HandoffFailure = new TestDomainException();
        var row = ManualRow(bed.Inbox);

        var outcome = await bed.Processor().ProcessAsync(row, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.RunFailed, outcome);
        Assert.Equal([$"complete {row.MessageId} 1 run_failed"], bed.Inbox.Calls);
    }

    /// <summary>Review Focus 4: el entrante encolado antes del arranque ya arrancó una corrida. La fila manual la encuentra viva y se cierra
    /// sin enviar, sin tocar el entrante que la corrida registró (no es una respuesta del cliente) y sin reintentar.</summary>
    [Fact]
    public async Task AManualRowBehindALiveRunClosesWithoutTouchingIt()
    {
        var bed = new Bed(null, ActiveState());
        var run = WaitingRun(ManualNow.AddMinutes(-1));
        var inboundId = Guid.CreateVersion7();
        run.MarkInbound(inboundId);
        bed.Runs.Added.Add(run);
        var row = ManualRow(bed.Inbox);

        var outcome = await bed.Processor().ProcessAsync(row, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal([$"complete {row.MessageId} 1 skipped"], bed.Inbox.Calls);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(inboundId, run.LastInboundMessageId);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Conversations.Handoffs);
    }

    /// <summary>Quien la tome o la resuelva antes de que el worker llegue a la fila manual la deja sin efecto: no envía nada.</summary>
    [Theory]
    [InlineData("advisor")]
    [InlineData("resolved")]
    [InlineData("paused-manual")]
    [InlineData("paused-advisor-took")]
    public async Task AManualRowOnAHeldOrResolvedConversationIsSkipped(string scenario)
    {
        var state = scenario switch
        {
            "advisor" => ActiveState() with { AssignedMemberId = Guid.CreateVersion7() },
            "resolved" => ActiveState() with { Status = "Resolved" },
            "paused-manual" => ActiveState() with { AutomationStatus = "Paused", AutomationPauseReason = "Manual" },
            _ => ActiveState() with { AutomationStatus = "Paused", AutomationPauseReason = "AdvisorTook" },
        };
        var bed = new Bed(null, state);
        var row = ManualRow(bed.Inbox);

        var outcome = await bed.Processor().ProcessAsync(row, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal([$"complete {row.MessageId} 1 skipped"], bed.Inbox.Calls);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Runs.Added);
    }

    private sealed class ManualBed
    {
        private readonly AutomationConversationState? state;
        private readonly bool moduleEnabled;
        private readonly string? flow;
        private readonly ChatbotStatus chatbotStatus;
        private readonly bool lockBusy;

        private ManualBed(AutomationConversationState? state, bool moduleEnabled, string? flow, ChatbotStatus chatbotStatus, bool lockBusy)
        {
            this.state = state;
            this.moduleEnabled = moduleEnabled;
            this.flow = flow;
            this.chatbotStatus = chatbotStatus;
            this.lockBusy = lockBusy;
        }

        public NoUnitOfWork Unit { get; } = new(null, false);

        public MemoryRuns Runs { get; } = new();

        public RecordingInbox Inbox { get; } = new();

        public ManualRunUnits Units { get; } = new();

        public static ManualBed For(ManualStartVerdict verdict, bool lockBusy = false, bool deadRun = false, string? flow = Flow)
        {
            var open = new AutomationConversationState(
                "Open", null, "Paused", null, ConnectionId, ManualNow, "Laura", "573001234567", null, "Manual", "WhatsApp", false, ManualNow.AddHours(1));
            var state = verdict switch
            {
                ManualStartVerdict.NotFound => null,
                ManualStartVerdict.NotOpen => open with { Status = "Resolved" },
                ManualStartVerdict.HasAdvisor => open with { AssignedMemberId = Guid.CreateVersion7() },
                ManualStartVerdict.Blocked => open with { Blocked = true },
                ManualStartVerdict.UnsupportedChannel => open with { Channel = "Instagram" },
                ManualStartVerdict.BotAttending => open with { AutomationStatus = "Active", AutomationChatbotId = ChatbotId },
                ManualStartVerdict.WindowClosed => open with { WindowExpiresAt = ManualNow.AddMinutes(-1) },
                _ => open,
            };
            var bed = new ManualBed(
                state, verdict != ManualStartVerdict.ModuleDisabled, flow,
                verdict == ManualStartVerdict.NoChatbot ? ChatbotStatus.Paused : ChatbotStatus.Active, lockBusy);
            bed.Inbox.PendingManualStart = verdict == ManualStartVerdict.StartPending;
            if (verdict == ManualStartVerdict.BotAttending || deadRun)
            {
                bed.Runs.Added.Add(WaitingRun(ManualNow.AddMinutes(-10)));
            }

            return bed;
        }

        public ManualRunStarter Starter() => new(
            lockBusy ? new NoUnitOfWork(null, true) : Unit, Runs, new ServingChatbots(flow, status: chatbotStatus), Inbox, new ManualConversations(state),
            new ModuleStatus(moduleEnabled), new RunUnitWriter(Units, new FixedClock(), NullLogger<RunUnitWriter>.Instance), new FixedClock());
    }

    private sealed class ManualConversations(AutomationConversationState? state, bool taken = false) : IAutomationConversations
    {
        /// <summary>Spec 2026-11-01 D-5: desde cuándo se preguntó si un asesor la tuvo.</summary>
        public List<DateTimeOffset> TakenChecks { get; } = [];

        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(state);

        public Task<bool> WasTakenSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken)
        {
            TakenChecks.Add(since);
            return Task.FromResult(taken);
        }

        public Task<bool> WasResolvedSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid? reasonId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualRunUnits : IRunUnitRepository
    {
        public List<RunUnitEntry> Entries { get; } = [];

        public void Add(RunUnitEntry entry) => Entries.Add(entry);

        public Task<IReadOnlyList<RunUnitEntry>> ListAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
