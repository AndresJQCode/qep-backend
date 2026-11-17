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

    public FakeCopilotRepository Repository { get; } = new();

    public FakeCopilotCredentials Credentials { get; } = new();

    public CountingUnitOfWork UnitOfWork { get; } = new();

    public CopilotSettingsCatalog Catalog { get; } = new();

    public FixedCopilotClock Clock { get; } = new(CopilotSettingsHandlersTests.Now);

    public GetCopilotSettingsHandler GetSettings(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), Repository, Credentials, Catalog, new BogotaClock(Clock));

    public UpdateCopilotSettingsHandler UpdateSettings(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), new UpdateCopilotSettingsValidator(), Repository, Credentials, Catalog, UnitOfWork, Clock,
            new BogotaClock(Clock));

    public GetCopilotStatusHandler GetStatus(IExecutionContext context) =>
        new(context, new FakeTenantModules(Modules), Repository, Credentials, Catalog, new BogotaClock(Clock));

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

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

/// <summary>El repositorio en memoria: la configuración y los hilos del tenant, filtrando por dueño como el real.</summary>
internal sealed class FakeCopilotRepository : ICopilotRepository
{
    public CopilotSettings? Settings { get; set; }

    public List<CopilotSettings> AddedSettings { get; } = [];

    public List<CopilotThread> Threads { get; } = [];

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
}
