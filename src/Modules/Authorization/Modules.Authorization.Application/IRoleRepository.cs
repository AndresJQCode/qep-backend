using Modules.Authorization.Domain;

namespace Modules.Authorization.Application;

public interface IRoleRepository
{
    Task<Role?> FindByIdAsync(RoleId id, Guid tenantId, CancellationToken cancellationToken);

    Task<Role?> FindByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    void Add(Role role);

    void Remove(Role role);
}

public interface IAuthorizationUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
