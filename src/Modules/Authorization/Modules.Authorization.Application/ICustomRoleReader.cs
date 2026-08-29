using Modules.Authorization.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Lee los roles que un tenant definio. Lo implementa Infrastructure.
/// </summary>
/// <remarks>
/// Existe como puerto para que <see cref="TenantRoleCatalog"/> —que decide quien puede que
/// cosa— no dependa de EF Core. `ArchitectureTests` verifica esa frontera.
/// </remarks>
public interface ICustomRoleReader
{
    Task<IReadOnlyCollection<Role>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
