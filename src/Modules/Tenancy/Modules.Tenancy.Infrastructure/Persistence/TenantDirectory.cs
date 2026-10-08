using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantDirectory(TenancyDbContext dbContext) : ITenantDirectory
{
    public async Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Slug)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.TimeZone)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.DefaultCurrency)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.LogoFileId)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => (TenantStatus?)tenant.Status)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<TenantRegionalSettings?> GetRegionalSettingsAsync(
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantRegionalSettings(
                tenant.TimeZone,
                tenant.DateFormat,
                tenant.DefaultCurrency,
                tenant.NumberFormat))
            .SingleOrDefaultAsync(cancellationToken);
}
