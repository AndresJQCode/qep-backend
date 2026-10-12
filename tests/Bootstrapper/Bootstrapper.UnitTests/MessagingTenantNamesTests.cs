using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-12 §3.3 (D-Q5): <c>variables.empresa</c> es el <c>DisplayName</c> del tenant, leído por
/// <see cref="ITenantDirectory"/> sin que Messaging conozca la persistencia de Tenancy. Sin fila, <c>null</c> (el handler lo vuelve <c>""</c>).</summary>
public sealed class MessagingTenantNamesTests
{
    [Fact]
    public async Task TheCompanyIsTheTenantsDisplayName()
    {
        var tenantId = Guid.CreateVersion7();
        var directory = new FakeTenantDirectory(new Dictionary<Guid, string> { [tenantId] = "Ritual Café" });

        var name = await new MessagingTenantNames(directory).FindAsync(tenantId, TestContext.Current.CancellationToken);

        Assert.Equal("Ritual Café", name);
    }

    [Fact]
    public async Task ATenantWithoutARowHasNoName()
    {
        var directory = new FakeTenantDirectory(new Dictionary<Guid, string>());

        Assert.Null(await new MessagingTenantNames(directory).FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    private sealed class FakeTenantDirectory(Dictionary<Guid, string> names) : ITenantDirectory
    {
        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(names.TryGetValue(tenantId.Value, out var name) ? name : null);

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
