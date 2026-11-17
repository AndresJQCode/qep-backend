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

    private static ListAiModelsHandler Handler(IExecutionContext context, ITenantModules? modules = null) =>
        new(context, modules ?? new FakeTenantModules(TenantModuleSet.FromStored(TenantModuleKeys.All)), new FakeCatalog());

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

internal sealed class FakeTenantModules(TenantModuleSet? set) : ITenantModules
{
    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(set);
}
