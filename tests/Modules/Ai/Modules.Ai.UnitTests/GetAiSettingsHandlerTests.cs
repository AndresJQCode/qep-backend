using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, C-1 y C-18: <c>GET /ai/settings</c> dice, por proveedor, si se puede conectar y cómo está su conexión. La
/// clave nunca viaja: el final visible (los últimos 4) lo muestra Integraciones, donde se administra.</summary>
public sealed class GetAiSettingsHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");

    [Fact]
    public async Task ItReturnsOneEntryPerProvider()
    {
        var connection = Guid.CreateVersion7();
        var credentials = new FakeCredentials(
        [
            new AiProviderConnectionState(AiProviders.Anthropic, Connectable: true, connection, "Active"),
            new AiProviderConnectionState(AiProviders.OpenAi, Connectable: false, null, null),
        ]);

        var settings = await new GetAiSettingsHandler(new FakeExecutionContext(Tenant, AiPermissions.AgentRead), AllModules(), credentials)
            .HandleAsync(new GetAiSettingsQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new AiProviderSettingsDto("anthropic", Connectable: true, connection, "Active"),
                new AiProviderSettingsDto("openai", Connectable: false, null, null),
            ],
            settings.Providers);
    }

    [Fact]
    public async Task WithoutThePermissionItIsDenied()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new GetAiSettingsHandler(new FakeExecutionContext(Tenant), AllModules(), new FakeCredentials([]))
                .HandleAsync(new GetAiSettingsQuery(Tenant), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    private static FakeTenantModules AllModules() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    private sealed class FakeCredentials(IReadOnlyList<AiProviderConnectionState> states) : IAiProviderCredentials
    {
        public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(states);

        public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
