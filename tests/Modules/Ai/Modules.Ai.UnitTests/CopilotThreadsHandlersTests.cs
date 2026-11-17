using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 (copiloto) §7, §8 y D-C11, plan fase 2 C1.5 y adenda D-7/D-11: los hilos privados por HTTP, con los topes por
/// miembro y del tenant. Un hilo de otro miembro es 404, nunca 403.</summary>
public sealed class CopilotThreadsHandlersTests
{
    private static readonly Guid Tenant = CopilotSettingsHandlersTests.Tenant;
    private static readonly Guid OtherMember = Guid.Parse("01900000-0000-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = CopilotSettingsHandlersTests.Now;
    private static readonly CopilotLimits Limits = new(MessagesPerHour: 30, MessagesPerDay: 200);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingAThreadEnqueuesTheFirstAnswer()
    {
        var bed = Enabled();

        var thread = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "¿Cuánto vendimos este mes?"), Ct);

        var stored = Assert.Single(bed.Repository.Threads);
        Assert.Equal((Member, stored.Id, "¿Cuánto vendimos este mes?"), (stored.UserId, thread.Id, thread.Title));
        Assert.True(thread.Pending);
        Assert.Equal(["User", "Assistant"], thread.Messages.Select(message => message.Role));
        Assert.Equal(["Done", "Pending"], thread.Messages.Select(message => message.Status));
        var job = Assert.Single(bed.Jobs.Added);
        Assert.Equal((Tenant, (Guid?)stored.Id, (Guid?)null, (Guid?)null, 1), (job.TenantId, job.CopilotThreadId, job.SessionId, job.TestId, job.UpToSeq));
        Assert.Equal((Guid?)job.Id, stored.Messages[^1].JobId);
        // Una sola transacción: el mensaje, la respuesta pendiente y el trabajo se guardan juntos.
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherMembersThreadIs404()
    {
        var bed = Enabled();
        var foreign = CopilotThread.Create(Guid.CreateVersion7(), Tenant, OtherMember, "Mi hilo", Guid.CreateVersion7(), Now);
        bed.Repository.Threads.Add(foreign);

        var errors = new List<ResourceNotFoundException>
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Get().HandleAsync(new GetCopilotThreadQuery(Tenant, foreign.Id), Ct)),
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                bed.Send().HandleAsync(new SendCopilotMessageCommand(Tenant, foreign.Id, "¿Y ahora?"), Ct)),
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                bed.Rename().HandleAsync(new RenameCopilotThreadCommand(Tenant, foreign.Id, 1, "Mío"), Ct)),
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                bed.Delete().HandleAsync(new DeleteCopilotThreadCommand(Tenant, foreign.Id), Ct)),
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                bed.Retry().HandleAsync(new RetryCopilotMessageCommand(Tenant, foreign.Id, foreign.Messages[^1].Id), Ct)),
        };

        Assert.All(errors, error => Assert.Equal(AiErrorCodes.CopilotThreadNotFound, error.Code));
        Assert.Empty(bed.Repository.Deleted);
        Assert.Equal("Mi hilo", foreign.Title);
        Assert.Empty((await bed.List().HandleAsync(new ListCopilotThreadsQuery(Tenant, null), Ct)).Items);
    }

    [Fact]
    public async Task UseIsRequired()
    {
        var bed = Enabled();
        bed.Context = new FakeExecutionContext(Tenant, AiPermissions.CopilotManage);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task ASecondQuestionWhilePendingIsBusy()
    {
        var bed = Enabled();
        var thread = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "¿Cuánto vendimos?"), Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            bed.Send().HandleAsync(new SendCopilotMessageCommand(Tenant, thread.Id, "¿Y el mes pasado?"), Ct));

        Assert.Equal(AiErrorCodes.CopilotBusy, error.Code);
        Assert.Single(bed.Jobs.Added);
    }

    [Fact]
    public async Task ASecondQuestionAfterTheAnswerIsAccepted()
    {
        var bed = Enabled();
        var created = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "¿Cuánto vendimos?"), Ct);
        var stored = bed.Repository.Threads[0];
        stored.CompleteAssistant(bed.Jobs.Added[0].Id, "Doce.", [], Now);

        var sent = await bed.Send().HandleAsync(new SendCopilotMessageCommand(Tenant, created.Id, "¿Y el mes pasado?"), Ct);

        Assert.True(sent.Pending);
        Assert.Equal(4, sent.Messages.Count);
        Assert.Equal(3, bed.Jobs.Added[^1].UpToSeq);
    }

    [Fact]
    public async Task WithoutConfigurationAskingIs422Disabled()
    {
        var bed = new ThreadsBed();

        var error = await Assert.ThrowsAsync<AiDomainException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct));

        Assert.Equal(AiErrorCodes.CopilotDisabled, error.Code);
        Assert.Empty(bed.Repository.Threads);
    }

    [Fact]
    public async Task TheHourlyLimitIs429WithRetryAfter()
    {
        var bed = Enabled();
        // 30 preguntas en la última hora; la más vieja hace 50 minutos: la siguiente cabe en 10.
        bed.Repository.QuestionTimes.AddRange(Enumerable.Range(0, 30).Select(index => Now.AddMinutes(-50).AddSeconds(index)));

        var error = await Assert.ThrowsAsync<CopilotRateLimitedException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Una más"), Ct));

        Assert.Equal(AiErrorCodes.CopilotRateLimited, error.Code);
        Assert.Equal(TimeSpan.FromMinutes(10), error.RetryAfter);
        Assert.IsAssignableFrom<RequestRateLimitedException>(error);
        Assert.Equal(600, Assert.IsAssignableFrom<IHasProblemExtensions>(error).ProblemExtensions["retryAfterSeconds"]);
        Assert.Empty(bed.Repository.Threads);
    }

    [Fact]
    public async Task TheDailyLimitIs429UntilTheOldestQuestionLeavesTheDay()
    {
        var bed = Enabled();
        bed.Repository.QuestionTimes.AddRange(Enumerable.Range(0, 200).Select(index => Now.AddHours(-23).AddMinutes(index % 50)));

        var error = await Assert.ThrowsAsync<CopilotRateLimitedException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Una más"), Ct));

        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
    }

    [Fact]
    public async Task TheMonthlyBudgetIs429()
    {
        var bed = Enabled(budget: 1000);
        bed.Repository.Usage = new CopilotTokenUsage(700, 300);

        var error = await Assert.ThrowsAsync<CopilotRateLimitedException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct));

        Assert.Equal(AiErrorCodes.CopilotBudgetExhausted, error.Code);
        // Hasta el 1 de diciembre a las 00:00 de Bogotá (05:00 UTC).
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero) - Now, error.RetryAfter);
        Assert.True((int)Assert.IsAssignableFrom<IHasProblemExtensions>(error).ProblemExtensions["retryAfterSeconds"]! > 0);
    }

    [Fact]
    public async Task WithoutTheProviderConnectionAskingIs422ProviderNotConfigured()
    {
        var bed = Enabled();
        bed.Credentials.States =
        [
            new AiProviderConnectionState(AiProviders.Anthropic, true, Guid.CreateVersion7(), "Paused"),
            new AiProviderConnectionState(AiProviders.OpenAi, true, null, null),
        ];

        var error = await Assert.ThrowsAsync<AiProviderNotConfiguredException>(() =>
            bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct));

        Assert.Equal("anthropic", error.ProblemExtensions["provider"]);
    }

    [Fact]
    public async Task AQuestionOver2000IsAValidationError()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Enabled().Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, new string('a', 2001)), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Text");
    }

    [Fact]
    public async Task DeletingTakesTheToolCalls()
    {
        var bed = Enabled();
        var thread = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct);

        Assert.True(await bed.Delete().HandleAsync(new DeleteCopilotThreadCommand(Tenant, thread.Id), Ct));

        // El repositorio borra en un comando los tool_calls de los trabajos del hilo y el hilo con sus mensajes (D-C8).
        Assert.Equal([thread.Id], bed.Repository.Deleted);
        Assert.Empty(bed.Repository.Threads);
    }

    [Fact]
    public async Task RenamingNeedsTheCurrentVersion()
    {
        var bed = Enabled();
        var thread = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct);

        var renamed = await bed.Rename().HandleAsync(new RenameCopilotThreadCommand(Tenant, thread.Id, thread.Version, "Ventas"), Ct);
        var stale = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.Rename().HandleAsync(new RenameCopilotThreadCommand(Tenant, thread.Id, thread.Version, "Otra"), Ct));

        Assert.Equal(("Ventas", thread.Version + 1), (renamed.Title, renamed.Version));
        Assert.Equal("concurrency.conflict", stale.Code);
    }

    [Fact]
    public async Task OnlyTheLastFailedAnswerIsRetried()
    {
        var bed = Enabled();
        var created = await bed.Create().HandleAsync(new CreateCopilotThreadCommand(Tenant, "Hola"), Ct);
        var stored = bed.Repository.Threads[0];
        var answerId = stored.Messages[^1].Id;

        var whilePending = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            bed.Retry().HandleAsync(new RetryCopilotMessageCommand(Tenant, created.Id, answerId), Ct));
        Assert.Equal(AiErrorCodes.CopilotBusy, whilePending.Code);

        stored.FailAssistant(bed.Jobs.Added[0].Id, "job_timed_out", Now);
        var notTheLast = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            bed.Retry().HandleAsync(new RetryCopilotMessageCommand(Tenant, created.Id, stored.Messages[0].Id), Ct));
        Assert.Equal(AiErrorCodes.CopilotNotRetryable, notTheLast.Code);

        var retried = await bed.Retry().HandleAsync(new RetryCopilotMessageCommand(Tenant, created.Id, answerId), Ct);

        Assert.True(retried.Pending);
        Assert.Equal(2, bed.Jobs.Added.Count);
        Assert.Equal((Guid?)bed.Jobs.Added[^1].Id, stored.Messages[^1].JobId);
    }

    [Fact]
    public async Task TheListIsTheMembersOwnByActivityWithVersionAndACursor()
    {
        var bed = Enabled();
        for (var index = 0; index < 31; index++)
        {
            bed.Repository.Threads.Add(CopilotThread.Create(
                Guid.CreateVersion7(), Tenant, Member, $"Hilo {index}", Guid.CreateVersion7(), Now.AddMinutes(-index)));
        }

        bed.Repository.Threads.Add(CopilotThread.Create(Guid.CreateVersion7(), Tenant, OtherMember, "Ajeno", Guid.CreateVersion7(), Now));

        var first = await bed.List().HandleAsync(new ListCopilotThreadsQuery(Tenant, null), Ct);
        var second = await bed.List().HandleAsync(new ListCopilotThreadsQuery(Tenant, first.NextCursor), Ct);

        Assert.Equal(30, first.Items.Count);
        Assert.Equal("Hilo 0", first.Items[0].Title);
        Assert.All(first.Items, item => Assert.Equal(1L, item.Version));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["Hilo 30"], second.Items.Select(item => item.Title));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task AForeignCursorIsAValidationError()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Enabled().List().HandleAsync(new ListCopilotThreadsQuery(Tenant, "no-es-un-cursor"), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "cursor");
    }

    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-000000000002");

    private static ThreadsBed Enabled(long? budget = null)
    {
        var bed = new ThreadsBed();
        var settings = CopilotSettings.Unconfigured(Tenant);
        settings.Update(true, "anthropic", "claude-opus-5-5", string.Empty, budget, 90, Guid.NewGuid(), Now);
        bed.Repository.Settings = settings;
        return bed;
    }

    private sealed class ThreadsBed
    {
        private readonly CopilotBed _bed = new();

        public IExecutionContext Context { get; set; } = new FakeExecutionContext(Tenant, AiPermissions.CopilotUse);

        public FakeCopilotRepository Repository => _bed.Repository;

        public FakeCopilotCredentials Credentials => _bed.Credentials;

        public CountingUnitOfWork UnitOfWork => _bed.UnitOfWork;

        public CapturingModelJobs Jobs { get; } = new();

        public CreateCopilotThreadHandler Create() =>
            new(Context, Modules(), new CreateCopilotThreadValidator(), Asking(), Repository, Jobs, UnitOfWork, _bed.Clock);

        public SendCopilotMessageHandler Send() =>
            new(Context, Modules(), new SendCopilotMessageValidator(), Asking(), Repository, Jobs, UnitOfWork, _bed.Clock);

        public RetryCopilotMessageHandler Retry() => new(Context, Modules(), Asking(), Repository, Jobs, UnitOfWork, _bed.Clock);

        public GetCopilotThreadHandler Get() => new(Context, Modules(), Repository);

        public ListCopilotThreadsHandler List() => new(Context, Modules(), new ListCopilotThreadsValidator(), Repository);

        public RenameCopilotThreadHandler Rename() => new(Context, Modules(), new RenameCopilotThreadValidator(), Repository, UnitOfWork);

        public DeleteCopilotThreadHandler Delete() => new(Context, Modules(), Repository);

        private FakeTenantModules Modules() => new(_bed.Modules);

        private CopilotAsking Asking() => new(Repository, Credentials, _bed.Catalog, Limits, _bed.TenantClock);
    }
}

internal sealed class CapturingModelJobs : IAiModelJobs
{
    public List<ModelJob> Added { get; } = [];

    public void Add(ModelJob job) => Added.Add(job);

    public Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RetryHeldAsync(Guid jobId, DateTimeOffset retryAt, CancellationToken cancellationToken) => throw new NotSupportedException();
}
