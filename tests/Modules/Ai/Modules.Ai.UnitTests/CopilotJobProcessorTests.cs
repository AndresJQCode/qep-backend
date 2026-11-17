using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan fase 2, C2.3: el trabajo del copiloto en el worker (spec 2026-11-17 copiloto §7), con el bucle real y dobles alrededor.</summary>
public sealed class CopilotJobProcessorTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid AnthropicConnection = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeCopilotRepository _repository = new();
    private readonly FakeCopilotCredentials _credentials = new();
    private readonly LoopModelClient _model = new();
    private readonly LoopResolver _resolver;
    private readonly LoopLedger _ledger = new();
    private readonly LoopCredentials _marks = new();
    private readonly FakeCopilotTools _tools = new();
    private readonly HeldJobs _jobs = new();
    private readonly FailingUnitOfWork _unitOfWork = new();
    private readonly FakeCopilotMemberNames _names = new();
    private TenantModuleSet _modules = TenantModuleSet.FromStored(TenantModuleKeys.All);

    public CopilotJobProcessorTests()
    {
        _resolver = new LoopResolver(_model);
        _repository.Settings = CopilotSettings.Unconfigured(Tenant);
        _repository.Settings.Update(true, "anthropic", "claude-opus-5-5", "Somos una distribuidora.", null, 90, Member, Now);
    }

    [Fact]
    public async Task AnAnswerCompletesTheMessageWithItsCitations()
    {
        var (thread, job) = Ask();
        var order = Guid.NewGuid().ToString();
        _tools.Results["search_orders"] = CopilotToolResult.Ok(new { items = new[] { new { number = "P-1" } } }, [new CopilotCitation("order", order, "P-1")]);
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("call_1", "search_orders", new { query = "acme" })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Acme tiene P-1.", citations = new[] { order } })));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        var answer = thread.Messages[^1];
        Assert.Equal((CopilotMessageStatus.Done, "Acme tiene P-1.", null), (answer.Status, answer.Text, answer.Progress));
        Assert.Equal([new CopilotCitation("order", order, "P-1")], answer.Citations);
        Assert.Equal((ModelJobStatus.Done, "answered"), (job.Status, job.Outcome));
        Assert.Equal([new CopilotPrincipal(Tenant, Member)], _tools.Executed.Select(call => call.Principal));
        Assert.Equal("claude-opus-5-5", _model.Requests[0].Model);
    }

    // Spec §6 (C-12) y plan C3.4: el nombre de quien pregunta va en el contexto volátil, nunca en el system (que se cachea).
    [Fact]
    public async Task TheVolatileContextNamesTheMember()
    {
        var (_, job) = Ask();
        _names.Names[Member] = "Ana";
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Contains("<miembro>Ana</miembro>", _model.Requests[0].VolatileContext, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", _model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal([Member], _names.Asked);
    }

    [Fact]
    public async Task AnInactiveMemberFailsTheAnswer()
    {
        var (thread, job) = Ask();
        _tools.Available = null;

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal((CopilotMessageStatus.Failed, CopilotToolErrors.MemberInactive), (thread.Messages[^1].Status, thread.Messages[^1].ErrorCode));
        Assert.Equal((ModelJobStatus.Failed, CopilotToolErrors.MemberInactive), (job.Status, job.ErrorCode));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task TheModuleOffFailsTheAnswer()
    {
        var (thread, job) = Ask();
        _modules = TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Copilot));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal((CopilotMessageStatus.Failed, CopilotJobProcessor.ModuleDisabled), (thread.Messages[^1].Status, thread.Messages[^1].ErrorCode));
        Assert.Empty(_model.Requests);
        Assert.Empty(_tools.Listed);
    }

    [Fact]
    public async Task ProgressIsWrittenBeforeEachTool()
    {
        var (thread, job) = Ask();
        _tools.Results["search_orders"] = CopilotToolResult.Ok(new { items = Array.Empty<object>() }, []);
        _tools.Results["get_orders_summary"] = CopilotToolResult.Ok(new { orderCount = 0 }, []);
        _tools.OnExecute = name => Assert.Equal((thread.Id, job.Id, name), _repository.ProgressWrites[^1]);
        _model.Enqueue(LoopResponses.Tools(
            LoopResponses.Call("call_1", "search_orders", new { query = "x" }), LoopResponses.Call("call_2", "get_orders_summary", new { })));
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Nada.", citations = Array.Empty<string>() })));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(["search_orders", "get_orders_summary"], _repository.ProgressWrites.Select(write => write.Progress));
        Assert.Null(thread.Messages[^1].Progress);
    }

    // §7 paso 3 y gotcha RememberResult: la respuesta (pagada) se guarda antes del commit corto; si el commit falla, el reintento la usa.
    [Fact]
    public async Task ACommitFailureNeverPaysTheModelAgain()
    {
        var (thread, job) = Ask();
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));
        _unitOfWork.FailOnSave = 2;

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.NotNull(job.ResultJson);
        Assert.Equal([job.Id], _jobs.Held);
        Assert.Single(_model.Requests);

        // La transacción del commit se deshizo: el hilo vuelve a estar como en la base, con la respuesta pendiente.
        _repository.Threads.Clear();
        var reloaded = CopilotThread.Create(thread.Id, Tenant, Member, "¿Cuánto vendimos?", job.Id, Now);
        _repository.Threads.Add(reloaded);
        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Single(_model.Requests);
        Assert.Equal((CopilotMessageStatus.Done, "Hola."), (reloaded.Messages[^1].Status, reloaded.Messages[^1].Text));
        Assert.Equal(ModelJobStatus.Done, job.Status);
    }

    [Fact]
    public async Task ADeletedThreadDiscardsTheJob()
    {
        var (thread, job) = Ask();
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));
        _repository.OnRead = () => _repository.Threads.Remove(thread);

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal((ModelJobStatus.Discarded, CopilotJobProcessor.ThreadGone), (job.Status, job.Outcome));
        // Lo gastado queda anotado aunque la respuesta ya no tenga dónde ir.
        Assert.Single(_ledger.Usage);
    }

    [Fact]
    public async Task AJobOfAThreadThatNoLongerExistsIsDiscardedWithoutCallingTheModel()
    {
        var (thread, job) = Ask();
        _repository.Threads.Remove(thread);

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(ModelJobStatus.Discarded, job.Status);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task AQuotaErrorFailsWithItsCodeAndMarksTheConnection()
    {
        var (thread, job) = Ask();
        _model.Enqueue(new AiQuotaExhaustedException());

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(AiModelErrorCodes.QuotaExhausted, thread.Messages[^1].ErrorCode);
        Assert.Equal([(Tenant, AnthropicConnection, AiCredentialFailures.QuotaExhausted)], _marks.Marked);
    }

    [Fact]
    public async Task ATransientErrorIsRetriedAndTheMessageStaysPending()
    {
        var (thread, job) = Ask();
        _model.Enqueue(new AiRateLimitedException(TimeSpan.FromSeconds(9)));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal((ModelJobStatus.Pending, Now.AddSeconds(9)), (job.Status, job.NextAttemptAt));
        Assert.Equal(CopilotMessageStatus.Pending, thread.Messages[^1].Status);
    }

    // C-20: la configuración elige OpenAI, que no tiene cuenta; Anthropic sí. El copiloto no cae a Anthropic.
    [Fact]
    public async Task TheCopilotNeverFallsBackToTheOtherProvider()
    {
        _repository.Settings!.Update(true, "openai", "gpt-6-luna", "", null, 90, Member, Now);
        var (thread, job) = Ask();

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(AiErrorCodes.ProviderNotConfigured, thread.Messages[^1].ErrorCode);
        Assert.Empty(_resolver.Asked);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task ATenantInstructionsAndTheThreadReachThePrompt()
    {
        var (_, job) = Ask();
        _model.Enqueue(LoopResponses.Tools(LoopResponses.Call("final", CopilotToolNames.Answer, new { text = "Hola.", citations = Array.Empty<string>() })));

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        var request = _model.Requests.Single();
        Assert.Contains("<contexto_del_negocio>Somos una distribuidora.</contexto_del_negocio>", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal([new AiChatMessage(AiChatRole.User, "¿Cuánto vendimos?")], request.Messages);
    }

    [Fact]
    public async Task AJobClaimedTooManyTimesFailsWithoutCallingTheModel()
    {
        var (thread, job) = Ask();
        typeof(ModelJob).GetProperty(nameof(ModelJob.Attempts))!.SetValue(job, ModelJob.MaxClaims + 1);

        await Processor().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(AgentHandoffCodes.JobTimedOut, thread.Messages[^1].ErrorCode);
        Assert.Empty(_model.Requests);
    }

    private (CopilotThread Thread, ModelJob Job) Ask()
    {
        var jobId = Guid.NewGuid();
        var thread = CopilotThread.Create(Guid.NewGuid(), Tenant, Member, "¿Cuánto vendimos?", jobId, Now);
        _repository.Threads.Add(thread);
        // El reclamo del worker deja el primer intento.
        var job = ModelJob.ForCopilot(jobId, Tenant, thread.Id, thread.Messages[^2].Seq, Now);
        typeof(ModelJob).GetProperty(nameof(ModelJob.Attempts))!.SetValue(job, 1);
        return (thread, job);
    }

    private CopilotJobProcessor Processor()
    {
        var limits = new CopilotRunLimits(new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 8, 12, 20, null));
        var clock = new LoopClock { UtcNow = Now };
        return new CopilotJobProcessor(
            _repository,
            _jobs,
            _unitOfWork,
            new FakeTenantModules(_modules),
            _credentials,
            new CopilotSettingsCatalog(),
            new FixedTenantClock(Now),
            new CopilotPromptBuilder(limits),
            new CopilotTurnRunner(_resolver, _ledger, _marks, _tools, limits, clock, NullLogger<CopilotTurnRunner>.Instance),
            _names,
            clock,
            NullLogger<CopilotJobProcessor>.Instance);
    }

    private sealed class FixedTenantClock(DateTimeOffset now) : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(now, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class HeldJobs : IAiModelJobs
    {
        public List<Guid> Held { get; } = [];

        public void Add(ModelJob job) => throw new NotSupportedException();

        public Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetryHeldAsync(Guid jobId, DateTimeOffset retryAt, CancellationToken cancellationToken)
        {
            Held.Add(jobId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Falla el guardado número <see cref="FailOnSave"/> con el 412 de una carrera, como lo traduce <c>AiUnitOfWork</c>.</summary>
    private sealed class FailingUnitOfWork : IAiUnitOfWork
    {
        private int _saves;

        public int? FailOnSave { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _saves++;
            return _saves == FailOnSave
                ? throw new RequestConcurrencyException("concurrency.conflict", "The copilot thread changed.")
                : Task.CompletedTask;
        }
    }
}
