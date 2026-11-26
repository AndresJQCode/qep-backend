using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, C-3: <c>GET /ai/models</c> responde la lista curada agrupada por proveedor. Los permisos son de núcleo, así
/// que el handler revalida tenant, permiso y capacidad <c>ai</c> (403 con código, nunca 404).</summary>
public sealed class ListAiModelsHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");

    [Fact]
    public async Task ItListsEveryProviderInContractOrderWithItsModels()
    {
        var result = await Handler(new FakeExecutionContext(Tenant, AiPermissions.AgentRead))
            .HandleAsync(new ListAiModelsQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal("anthropic", result.DefaultProvider);
        Assert.Equal(["anthropic", "openai"], result.Providers.Select(provider => provider.Provider));
        var anthropic = result.Providers[0];
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], anthropic.Models.Select(model => model.Id));
        Assert.True(anthropic.Models[0].IsDefault);
        Assert.False(anthropic.Models[1].IsDefault);
        Assert.False(Assert.Single(result.Providers[1].Models).Available);
    }

    // Plan 2026-11-17 A2 (C-3, F-3): cada proveedor dice si se puede usar y, si no, por qué; una fila por estado de la conexión.
    [Theory]
    [InlineData(true, "Active", null, true, null)]
    [InlineData(true, null, null, false, "not_configured")]
    [InlineData(false, null, null, false, "not_configured")]
    [InlineData(true, "Paused", null, false, "paused")]
    [InlineData(true, "NeedsAttention", "quota_exhausted", false, "quota_exhausted")]
    [InlineData(true, "NeedsAttention", "credentials_rejected", false, "credentials_rejected")]
    [InlineData(true, "NeedsAttention", null, false, "needs_attention")]
    [InlineData(true, "NeedsAttention", "", false, "needs_attention")]
    public async Task EachProviderSaysWhetherItIsConfiguredAndWhy(
        bool connectable, string? status, string? failureCode, bool configured, string? issue)
    {
        var credentials = new FakeAiProviderCredentials(
        [
            new AiProviderConnectionState(
                AiProviders.Anthropic, connectable, status is null ? null : Guid.CreateVersion7(), status, failureCode),
            new AiProviderConnectionState(AiProviders.OpenAi, Connectable: true, null, null),
        ]);

        var result = await Handler(new FakeExecutionContext(Tenant, AiPermissions.AgentRead), credentials: credentials)
            .HandleAsync(new ListAiModelsQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal((configured, issue), (result.Providers[0].Configured, result.Providers[0].Issue));
        Assert.Equal((false, "not_configured"), (result.Providers[1].Configured, result.Providers[1].Issue));
        Assert.Equal(1, credentials.DescribeCalls);
    }

    [Fact]
    public async Task AnotherTenantIsDenied()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Handler(new FakeExecutionContext(Guid.NewGuid(), AiPermissions.AgentRead))
            .HandleAsync(new ListAiModelsQuery(Tenant), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task WithoutThePermissionItIsDenied()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Handler(new FakeExecutionContext(Tenant))
            .HandleAsync(new ListAiModelsQuery(Tenant), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task WithTheModuleOffItIsModuleNotEnabled()
    {
        var modules = new FakeTenantModules(TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Ai)));
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Handler(new FakeExecutionContext(Tenant, AiPermissions.AgentRead), modules)
            .HandleAsync(new ListAiModelsQuery(Tenant), TestContext.Current.CancellationToken));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
    }

    private static ListAiModelsHandler Handler(
        IExecutionContext context, ITenantModules? modules = null, IAiProviderCredentials? credentials = null) =>
        new(
            context,
            modules ?? new FakeTenantModules(TenantModuleSet.FromStored(TenantModuleKeys.All)),
            new FakeCatalog(),
            credentials ?? new FakeAiProviderCredentials([]));

    private sealed class FakeCatalog : IAiModelCatalog
    {
        public AiProvider DefaultProvider => AiProviders.Anthropic;

        public IReadOnlyList<AiProviderModels> Providers { get; } =
        [
            new(AiProviders.Anthropic,
            [
                new("claude-opus-5-5", "Claude Opus 5.5", "El más capaz.", IsDefault: true, Available: true, Effort: "medium"),
                new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Rápido.", IsDefault: false, Available: true, Effort: "medium"),
            ]),
            new(AiProviders.OpenAi, [new("gpt-6-luna", "GPT-6 Luna", "", IsDefault: true, Available: false, Effort: null)]),
        ];
    }
}

internal sealed class FakeExecutionContext(Guid tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.Parse("01900000-0000-7000-8000-000000000002");

    public Modules.Tenancy.Domain.TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission);
}

/// <summary>Los estados de conexión de cada proveedor, sin claves: lo que <c>GET /ai/models</c> y el editor leen una vez por request.</summary>
internal sealed class FakeAiProviderCredentials(IReadOnlyList<AiProviderConnectionState> states) : IAiProviderCredentials
{
    public int DescribeCalls { get; private set; }

    public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        DescribeCalls++;
        return Task.FromResult(states);
    }

    public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeTenantModules(TenantModuleSet? set) : ITenantModules
{
    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(set);
}
