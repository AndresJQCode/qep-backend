using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

// Read-only cross-module lookup so other modules (e.g. one provisioning a tenant-scoped
// subdomain) can resolve a tenant's slug without taking a dependency on Tenancy's
// persistence or repeating the slug in their own schema.
public interface ITenantDirectory
{
    Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken);
}
