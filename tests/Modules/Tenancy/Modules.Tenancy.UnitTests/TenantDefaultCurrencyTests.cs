using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantDefaultCurrencyTests
{
    [Fact]
    public async Task GetAsyncReturnsTheStoredCurrency()
    {
        var tenantId = TenantId.New();
        var port = new TenantDefaultCurrency(new CurrencyDirectory(tenantId, "USD"));

        Assert.Equal("USD", await port.GetAsync(tenantId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsyncThrowsNotFoundForAnUnknownTenant()
    {
        var port = new TenantDefaultCurrency(new CurrencyDirectory(TenantId.New(), "USD"));

        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => port.GetAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("tenancy.tenant.not_found", missing.Code);
    }

    private sealed class CurrencyDirectory(TenantId known, string currency) : ITenantDirectory
    {
        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(tenantId == known ? currency : null);

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
