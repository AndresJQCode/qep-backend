using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 (copiloto) §8, plan fase 2 C1.4 y adenda D-10: <c>GET|PUT /copilot/settings</c> con <c>ai.copilot.manage</c> y
/// <c>GET /copilot/status</c> con <c>ai.copilot.use</c>, los dos detrás de la capacidad <c>copilot</c>.</summary>
public sealed class CopilotSettingsHandlersTests
{
    internal static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    internal static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManageIsRequiredForSettings()
    {
        var bed = new CopilotBed();

        var read = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetSettings(new FakeExecutionContext(Tenant, AiPermissions.CopilotUse)).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct));
        var write = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.UpdateSettings(new FakeExecutionContext(Tenant, AiPermissions.CopilotUse)).HandleAsync(Enable(0), Ct));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetSettings(new FakeExecutionContext(Guid.NewGuid(), AiPermissions.CopilotManage)).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct));

        Assert.All([read, write, otherTenant], error => Assert.Equal("authorization.denied", error.Code));
        Assert.Empty(bed.Repository.AddedSettings);
    }

    [Fact]
    public async Task WithoutTheCopilotModuleItIsModuleNotEnabled()
    {
        var bed = new CopilotBed { Modules = TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Copilot)) };

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetSettings(new FakeExecutionContext(Tenant, AiPermissions.CopilotManage)).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct));
        var status = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetStatus(new FakeExecutionContext(Tenant, AiPermissions.CopilotUse)).HandleAsync(new GetCopilotStatusQuery(Tenant), Ct));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, status.Code);
    }

    [Fact]
    public async Task WithoutARowTheSettingsAreTheFactoryOnesWithVersionZero()
    {
        var bed = new CopilotBed();

        var settings = await bed.GetSettings(Manager()).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct);

        Assert.Equal(
            (false, (string?)null, (string?)null, string.Empty, (long?)null, 90, 0L),
            (settings.Enabled, settings.Provider, settings.Model, settings.Instructions, settings.MonthlyTokenBudget, settings.RetentionDays,
                settings.Version));
        Assert.Equal((0L, 0L), (settings.UsageThisMonth.InputTokens, settings.UsageThisMonth.OutputTokens));
        // Todos los proveedores, en orden; los modelos, sólo de los que tienen conexión (D-9 del frontend).
        Assert.Equal(
            [("anthropic", true, (string?)"Active"), ("openai", false, (string?)null)],
            settings.Providers.Select(provider => (provider.Key, provider.Configured, provider.Status)));
        Assert.Equal(["anthropic"], settings.Models.Providers.Select(provider => provider.Provider));
    }

    [Fact]
    public async Task TheUsageIsTheCopilotsOfTheTenantMonth()
    {
        var bed = new CopilotBed();
        bed.Repository.Usage = new CopilotTokenUsage(1200, 300);

        var settings = await bed.GetSettings(Manager()).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct);

        Assert.Equal((1200L, 300L), (settings.UsageThisMonth.InputTokens, settings.UsageThisMonth.OutputTokens));
        // Bogotá es UTC-5: el mes del tenant empieza el 1 a las 05:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), bed.Repository.UsageSince);
    }

    // D-C12 y plan C3.4: el desglose del mes por miembro lo suma el backend, con el nombre resuelto; el que más gastó primero. Un miembro
    // que ya no se encuentra viaja igual, con un nombre de respaldo: su consumo es dinero del tenant.
    [Fact]
    public async Task TheUsageIsSummedPerMember()
    {
        var bed = new CopilotBed();
        var ana = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
        var gone = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
        bed.Repository.Usage = new CopilotTokenUsage(1200, 300);
        bed.Repository.ByMember = [new CopilotMemberTokenUsage(gone, 100, 50), new CopilotMemberTokenUsage(ana, 1100, 250)];
        bed.Names.Names[ana] = "Ana";

        var settings = await bed.GetSettings(Manager()).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct);

        Assert.Equal(
            [new CopilotMemberUsageDto(ana, "Ana", 1100, 250), new CopilotMemberUsageDto(gone, CopilotSettingsSupport.UnknownMemberName, 100, 50)],
            settings.UsageThisMonth.ByMember);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), bed.Repository.ByMemberSince);
        Assert.Equal([ana, gone], bed.Names.Asked.Order());
    }

    [Fact]
    public async Task WithoutUsageTheBreakdownIsEmptyAndNamesAreNotAsked()
    {
        var bed = new CopilotBed();

        var settings = await bed.GetSettings(Manager()).HandleAsync(new GetCopilotSettingsQuery(Tenant), Ct);

        Assert.NotNull(settings.UsageThisMonth.ByMember);
        Assert.Empty(settings.UsageThisMonth.ByMember);
        Assert.Empty(bed.Names.Asked);
    }

    // D-C13: guardar la configuración queda auditado con el miembro, como metadato (ni instrucciones ni modelo en la entrada).
    [Fact]
    public async Task SavingTheSettingsIsAudited()
    {
        var bed = new CopilotBed();
        var manager = Manager();

        await bed.UpdateSettings(manager).HandleAsync(Enable(0), Ct);

        var entry = Assert.Single(bed.Audit.Entries);
        Assert.Equal(
            (Tenant, (Guid?)manager.SubjectId, AiAuditActions.CopilotSettingsUpdated, AiAuditActions.ResourceCopilotSettings, Tenant),
            (entry.Tenant, entry.Actor, entry.Action, entry.ResourceType, entry.ResourceId));
    }

    [Fact]
    public async Task SavingCreatesTheRowAndReturnsTheNewVersion()
    {
        var bed = new CopilotBed();

        var saved = await bed.UpdateSettings(Manager()).HandleAsync(Enable(0), Ct);

        var row = Assert.Single(bed.Repository.AddedSettings);
        Assert.Equal((true, "anthropic", "claude-opus-5-5", 1L), (row.Enabled, row.Provider, row.Model, row.Version));
        Assert.Equal((true, 1L), (saved.Enabled, saved.Version));
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var bed = new CopilotBed();
        await bed.UpdateSettings(Manager()).HandleAsync(Enable(0), Ct);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.UpdateSettings(Manager()).HandleAsync(Enable(0), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task EnablingWithoutTheProviderConnectionIsRejected()
    {
        var bed = new CopilotBed();

        var error = await Assert.ThrowsAsync<AiProviderNotConfiguredException>(() =>
            bed.UpdateSettings(Manager()).HandleAsync(Enable(0) with { Provider = "openai", Model = "gpt-6-luna" }, Ct));

        Assert.Equal(AiErrorCodes.ProviderNotConfigured, error.Code);
        Assert.Equal("openai", error.ProblemExtensions["provider"]);
        Assert.Empty(bed.Repository.AddedSettings);

        // Apagado se puede dejar elegido aunque la cuenta todavía no esté conectada.
        var saved = await bed.UpdateSettings(Manager()).HandleAsync(Enable(0) with { Enabled = false, Provider = "openai", Model = "gpt-6-luna" }, Ct);
        Assert.Equal((false, "openai"), (saved.Enabled, saved.Provider));
    }

    [Theory]
    [InlineData("claude-haiku-9")]
    [InlineData("claude-retired")]
    public async Task AModelOutOfTheListIsRejected(string model)
    {
        var bed = new CopilotBed();

        var error = await Assert.ThrowsAsync<AiDomainException>(() =>
            bed.UpdateSettings(Manager()).HandleAsync(Enable(0) with { Model = model }, Ct));

        Assert.Equal(AiErrorCodes.CopilotModelNotAvailable, error.Code);
        Assert.Equal(["model"], error.FieldErrors.Keys);
        Assert.Empty(bed.Repository.AddedSettings);
    }

    [Theory]
    [InlineData(6, null, null, "RetentionDays")]
    [InlineData(366, null, null, "RetentionDays")]
    [InlineData(90, 0L, null, "MonthlyTokenBudget")]
    [InlineData(90, null, 4001, "Instructions")]
    public async Task TheValidatorMarksTheField(int retentionDays, long? budget, int? instructionsLength, string field)
    {
        var bed = new CopilotBed();
        var command = Enable(0) with
        {
            RetentionDays = retentionDays,
            MonthlyTokenBudget = budget,
            Instructions = instructionsLength is { } length ? new string('i', length) : string.Empty,
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.UpdateSettings(Manager()).HandleAsync(command, Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == field);
    }

    [Theory]
    [InlineData("not_configured")]
    [InlineData("disabled")]
    [InlineData("provider_issue")]
    [InlineData("budget_exhausted")]
    [InlineData(null)]
    public async Task StatusSaysWhyItIsUnavailable(string? reason)
    {
        var bed = new CopilotBed();
        if (reason != "not_configured")
        {
            var settings = CopilotSettings.Unconfigured(Tenant);
            settings.Update(
                reason != "disabled", "anthropic", "claude-opus-5-5", string.Empty, reason == "budget_exhausted" ? 1000 : null, 90,
                Guid.NewGuid(), Now);
            bed.Repository.Settings = settings;
        }

        if (reason == "provider_issue")
        {
            bed.Credentials.States =
            [
                new AiProviderConnectionState(AiProviders.Anthropic, true, Guid.CreateVersion7(), "NeedsAttention", "quota_exhausted"),
                new AiProviderConnectionState(AiProviders.OpenAi, true, null, null),
            ];
        }

        bed.Repository.Usage = new CopilotTokenUsage(800, 200);

        var status = await bed.GetStatus(new FakeExecutionContext(Tenant, AiPermissions.CopilotUse))
            .HandleAsync(new GetCopilotStatusQuery(Tenant), Ct);

        Assert.Equal((reason is null, reason), (status.Available, status.Reason));
        Assert.NotNull(status.Tools);
    }

    // Backend for frontend (§8): la lista es la del registro, calculada para quien llama; sin membresía activa, vacía.
    [Fact]
    public async Task StatusListsTheToolsTheCallerCanUseNow()
    {
        var bed = new CopilotBed();
        var caller = new FakeExecutionContext(Tenant, AiPermissions.CopilotUse);

        var status = await bed.GetStatus(caller).HandleAsync(new GetCopilotStatusQuery(Tenant), Ct);
        bed.Tools.Available = null;
        var inactive = await bed.GetStatus(caller).HandleAsync(new GetCopilotStatusQuery(Tenant), Ct);

        Assert.Equal(["search_orders", "get_orders_summary"], status.Tools);
        Assert.Equal([new CopilotPrincipal(Tenant, caller.SubjectId)], bed.Tools.Listed.Take(1));
        Assert.Empty(inactive.Tools);
    }

    [Fact]
    public async Task StatusNeedsUse()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new CopilotBed().GetStatus(new FakeExecutionContext(Tenant, AiPermissions.CopilotManage))
                .HandleAsync(new GetCopilotStatusQuery(Tenant), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    private static FakeExecutionContext Manager() => new(Tenant, AiPermissions.CopilotManage);

    private static UpdateCopilotSettingsCommand Enable(long version) =>
        new(Tenant, version, true, "anthropic", "claude-opus-5-5", "Somos una distribuidora.", null, 90);
}

/// <summary>Los dobles comunes de los handlers del copiloto.</summary>
internal sealed class CopilotBed
{
    public TenantModuleSet Modules { get; init; } = TenantModuleSet.FromStored(TenantModuleKeys.All);

    public CopilotBed() => Repository.Audit = Audit;

    public FakeCopilotRepository Repository { get; } = new();

    public FakeCopilotAudit Audit { get; } = new();

    public FakeCopilotMemberNames Names { get; } = new();

    public FakeCopilotCredentials Credentials { get; } = new();

    public CountingUnitOfWork UnitOfWork => field ??= new() { Log = Repository.AskingLog };

    public CopilotSettingsCatalog Catalog { get; } = new();

    public FixedCopilotClock Clock { get; } = new(CopilotSettingsHandlersTests.Now);

    public ITenantClock TenantClock => new BogotaClock(Clock);

    public GetCopilotSettingsHandler GetSettings(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), Repository, Credentials, Catalog, new BogotaClock(Clock), Names);

    public UpdateCopilotSettingsHandler UpdateSettings(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), new UpdateCopilotSettingsValidator(), Repository, Credentials, Catalog, UnitOfWork, Clock,
            new BogotaClock(Clock), Names, Audit);

    public FakeCopilotTools Tools { get; } = new();

    public GetCopilotStatusHandler GetStatus(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), Repository, Credentials, Catalog, new BogotaClock(Clock), Tools);

    internal sealed class FixedCopilotClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class BogotaClock(FixedCopilotClock clock) : ITenantClock
    {
        private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(clock.UtcNow, Bogota));
    }
}

internal sealed class CopilotSettingsCatalog : IAiModelCatalog
{
    public AiProvider DefaultProvider => AiProviders.Anthropic;

    public IReadOnlyList<AiProviderModels> Providers { get; } =
    [
        new(AiProviders.Anthropic,
        [
            new("claude-opus-5-5", "Claude Opus 5.5", "El más capaz.", IsDefault: true, Available: true, Effort: "medium"),
            new("claude-retired", "Claude viejo", "Retirado.", IsDefault: false, Available: false, Effort: "medium"),
        ]),
        new(AiProviders.OpenAi, [new("gpt-6-luna", "GPT-6 Luna", "", IsDefault: true, Available: true, Effort: null)]),
    ];
}

/// <summary>Anthropic conectada y activa, OpenAI sin cuenta, salvo que la prueba cambie <see cref="States"/>.</summary>
internal sealed class FakeCopilotCredentials : IAiProviderCredentials
{
    public IReadOnlyList<AiProviderConnectionState> States { get; set; } =
    [
        new(AiProviders.Anthropic, true, Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Active"),
        new(AiProviders.OpenAi, true, null, null),
    ];

    public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(States);

    public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken)
    {
        var state = States.Single(candidate => candidate.Provider == provider);
        return Task.FromResult(state is { Status: "Active", ConnectionId: { } id }
            ? AiCredentialResolution.Resolved(new AiCredential(id, "sk-test"))
            : AiCredentialResolution.Unusable(AiCredentialUnavailableReason.NotConfigured));
    }

    public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CountingUnitOfWork : IAiUnitOfWork
{
    public int Saves { get; private set; }

    /// <summary>Hallazgo 4: donde anotar cada guardado, junto a lo que hace el candado del miembro.</summary>
    public List<string>? Log { get; init; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        Log?.Add("save");
        return Task.CompletedTask;
    }
}

/// <summary>La auditoría del copiloto en memoria. <see cref="TakePending"/> imita el guardado: lo que el repositorio guarda junto al borrado.</summary>
internal sealed class FakeCopilotAudit : IAiAuditRecorder
{
    private int _pending;

    public List<(Guid Tenant, Guid? Actor, string Action, string ResourceType, Guid ResourceId)> Entries { get; } = [];

    public void Record(Guid tenantId, Guid actorId, string action, Guid agentId, DateTimeOffset at) => throw new NotSupportedException();

    public void RecordByAgent(Guid tenantId, string action, Guid agentId, DateTimeOffset at) => throw new NotSupportedException();

    public void RecordCopilot(Guid tenantId, Guid? actorId, string action, string resourceType, Guid resourceId, DateTimeOffset at)
    {
        Entries.Add((tenantId, actorId, action, resourceType, resourceId));
        _pending++;
    }

    public int TakePending()
    {
        var pending = _pending;
        _pending = 0;
        return pending;
    }
}

internal sealed class FakeCopilotMemberNames : ICopilotMemberNames
{
    public Dictionary<Guid, string> Names { get; } = [];

    public List<Guid> Asked { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        Asked.AddRange(userIds);
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            userIds.Where(Names.ContainsKey).ToDictionary(userId => userId, userId => Names[userId]));
    }
}

/// <summary>El repositorio en memoria: la configuración y los hilos del tenant, filtrando por dueño como el real.</summary>
internal sealed class FakeCopilotRepository : ICopilotRepository
{
    public CopilotSettings? Settings { get; set; }

    public List<CopilotSettings> AddedSettings { get; } = [];

    public List<CopilotThread> Threads { get; } = [];

    public List<Guid> Deleted { get; } = [];

    public List<DateTimeOffset> QuestionTimes { get; } = [];

    public CopilotTokenUsage Usage { get; set; } = new(0, 0);

    public DateTimeOffset? UsageSince { get; private set; }

    public Task<CopilotSettings?> FindSettingsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Settings is { } settings && settings.TenantId == tenantId ? settings : null);

    public void AddSettings(CopilotSettings settings)
    {
        AddedSettings.Add(settings);
        Settings = settings;
    }

    public Task<CopilotThread?> FindThreadAsync(Guid tenantId, Guid userId, Guid threadId, CancellationToken cancellationToken) =>
        Task.FromResult(Threads.SingleOrDefault(thread => thread.TenantId == tenantId && thread.UserId == userId && thread.Id == threadId));

    public void AddThread(CopilotThread thread) => Threads.Add(thread);

    public Task<CopilotTokenUsage> SumTokensAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        UsageSince = since;
        return Task.FromResult(Usage);
    }

    public Task<IReadOnlyList<CopilotThread>> ListThreadsAsync(
        Guid tenantId, Guid userId, CopilotThreadCursor? after, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CopilotThread>>(
        [
            .. Threads
                .Where(thread => thread.TenantId == tenantId && thread.UserId == userId)
                .OrderByDescending(thread => thread.LastActivityAt).ThenByDescending(thread => thread.Id)
                .Where(thread => after is null || thread.LastActivityAt < after.LastActivityAt
                    || (thread.LastActivityAt == after.LastActivityAt && thread.Id.CompareTo(after.Id) < 0))
                .Take(take),
        ]);

    /// <summary>Hallazgo 4: el candado del miembro, el conteo y el registro de preguntas, en orden.</summary>
    public List<string> AskingLog { get; } = [];

    public Task<IReadOnlyList<DateTimeOffset>> ListQuestionTimesAsync(
        Guid tenantId, Guid userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        AskingLog.Add("count");
        return Task.FromResult<IReadOnlyList<DateTimeOffset>>([.. QuestionTimes.Where(time => time >= since)]);
    }

    public Task<ICopilotAskingLock> LockAskingAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        AskingLog.Add($"lock {tenantId} {userId}");
        return Task.FromResult<ICopilotAskingLock>(new FakeAskingLock(AskingLog));
    }

    /// <summary>El registro de preguntas es aparte de los hilos: borrar un hilo no lo toca.</summary>
    public void AddQuestion(Guid tenantId, Guid userId, DateTimeOffset askedAt)
    {
        AskingLog.Add("question");
        QuestionTimes.Add(askedAt);
    }

    public DateTimeOffset? QuestionsBefore { get; private set; }

    public Task<int> DeleteQuestionsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        QuestionsBefore = before;
        return Task.FromResult(QuestionTimes.RemoveAll(time => time < before));
    }

    private sealed class FakeAskingLock(List<string> log) : ICopilotAskingLock
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            log.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            log.Add("release");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Hallazgo 8: con qué <c>skipLocked</c> se pidió cada borrado.</summary>
    public List<bool> SkipLockedAtDelete { get; } = [];

    public Task<bool> DeleteThreadAsync(Guid tenantId, Guid threadId, Action recordAudit, bool skipLocked, CancellationToken cancellationToken)
    {
        SkipLockedAtDelete.Add(skipLocked);
        Expired.RemoveAll(candidate => candidate.TenantId == tenantId && candidate.ThreadId == threadId);
        // Como el real: sin fila borrada (otro pod la borró o la tiene) no hay auditoría.
        if (Threads.RemoveAll(thread => thread.TenantId == tenantId && thread.Id == threadId) == 0)
        {
            return Task.FromResult(false);
        }

        Deleted.Add(threadId);
        recordAudit();
        // El real guarda lo pendiente (la auditoría) en la misma transacción del borrado.
        AuditPendingAtDelete.Add(Audit?.TakePending() ?? 0);
        return Task.FromResult(true);
    }

    public FakeCopilotAudit? Audit { get; set; }

    public List<int> AuditPendingAtDelete { get; } = [];

    public IReadOnlyList<CopilotMemberTokenUsage> ByMember { get; set; } = [];

    public DateTimeOffset? ByMemberSince { get; private set; }

    public Task<IReadOnlyList<CopilotMemberTokenUsage>> SumTokensByMemberAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ByMemberSince = since;
        return Task.FromResult(ByMember);
    }

    public List<CopilotExpiredThread> Expired { get; } = [];

    public DateTimeOffset? ExpiredAsOf { get; private set; }

    public Task<IReadOnlyList<CopilotExpiredThread>> ListExpiredThreadsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        ExpiredAsOf = now;
        return Task.FromResult<IReadOnlyList<CopilotExpiredThread>>([.. Expired.Take(take)]);
    }

    public int ExpiredToolCalls { get; set; }

    public DateTimeOffset? ToolCallsBefore { get; private set; }

    public Task<int> DeleteToolCallsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        ToolCallsBefore = before;
        return Task.FromResult(ExpiredToolCalls);
    }

    /// <summary>Lo que escribió el worker en <c>progress</c>, en orden.</summary>
    public List<(Guid ThreadId, Guid JobId, string Progress)> ProgressWrites { get; } = [];

    /// <summary>Corre después de la lectura sin tracking: la prueba borra el hilo «mientras el modelo piensa».</summary>
    public Action? OnRead { get; set; }

    public Task<CopilotThread?> ReadThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken)
    {
        var thread = Threads.SingleOrDefault(candidate => candidate.TenantId == tenantId && candidate.Id == threadId);
        OnRead?.Invoke();
        return Task.FromResult(thread);
    }

    public Task<CopilotThread?> FindThreadForJobAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken) =>
        Task.FromResult(Threads.SingleOrDefault(thread => thread.TenantId == tenantId && thread.Id == threadId));

    public Task SetProgressAsync(Guid tenantId, Guid threadId, Guid jobId, string progress, CancellationToken cancellationToken)
    {
        ProgressWrites.Add((threadId, jobId, progress));
        Threads.SingleOrDefault(thread => thread.TenantId == tenantId && thread.Id == threadId)?.SetProgress(jobId, progress);
        return Task.CompletedTask;
    }
}
