using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public interface ITenantRepository
{
    Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken);

    void Add(Tenant tenant);
}
