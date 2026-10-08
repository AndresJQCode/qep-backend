using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Dobles de la consola de operador (spec 2026-10-08). A mano, como el resto del repo: lo que la
// prueba no usa lanza, para que no pase por un camino que no creía estar ejerciendo.

internal sealed class FixedTenantDirectory(TenantStatus? status) : ITenantDirectory
{
    public List<TenantId> Asked { get; } = [];

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(status);
    }

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
