using Microsoft.EntityFrameworkCore;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;

namespace Modules.Authorization.Infrastructure.Persistence;

internal sealed class RoleRepository(AuthorizationDbContext dbContext) : IRoleRepository
{
    // Acotado al tenant, no solo por id: un id que existe en otra organizacion tiene que
    // responder "no encontrado" y no su contenido. Es el mismo criterio de `MembershipLoader`.
    public Task<Role?> FindByIdAsync(
        RoleId id,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        dbContext.Roles.SingleOrDefaultAsync(
            role => role.Id == id && role.TenantId == tenantId,
            cancellationToken);

    public Task<Role?> FindByKeyAsync(
        Guid tenantId,
        string key,
        CancellationToken cancellationToken) =>
        dbContext.Roles.SingleOrDefaultAsync(
            role => role.TenantId == tenantId && role.Key == key,
            cancellationToken);

    public void Add(Role role) => dbContext.Roles.Add(role);

    public void Remove(Role role) => dbContext.Roles.Remove(role);
}

/// <summary>
/// Lectura de solo lectura para <see cref="TenantRoleCatalog"/>.
/// </summary>
/// <remarks>
/// `AsNoTracking` a proposito: esto corre en la resolucion de permisos de cada request, y
/// trackear entidades que nadie va a modificar es trabajo y memoria por request para nada.
/// </remarks>
internal sealed class CustomRoleReader(AuthorizationDbContext dbContext) : ICustomRoleReader
{
    public async Task<IReadOnlyCollection<Role>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.Roles
            .AsNoTracking()
            .Where(role => role.TenantId == tenantId)
            .ToArrayAsync(cancellationToken);
}
