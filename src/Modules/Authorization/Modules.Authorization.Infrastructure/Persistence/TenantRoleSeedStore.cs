using Microsoft.EntityFrameworkCore;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Npgsql;

namespace Modules.Authorization.Infrastructure.Persistence;

/// <remarks>
/// No usa <see cref="AuthorizationUnitOfWork"/>: ahí un choque de clave es <c>authorization.role.key_taken</c>, y acá
/// es otra siembra que ganó la carrera.
/// </remarks>
internal sealed class TenantRoleSeedStore(AuthorizationDbContext dbContext) : ITenantRoleSeedStore
{
    public Task<bool> IsSeededAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.RoleSeeds.AnyAsync(seed => seed.TenantId == tenantId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> SeededTenantIdsAsync(CancellationToken cancellationToken) =>
        await dbContext.RoleSeeds
            .AsNoTracking()
            .Select(seed => seed.TenantId)
            .ToHashSetAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> RoleKeysAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Roles
            .AsNoTracking()
            .Where(role => role.TenantId == tenantId)
            .Select(role => role.Key)
            .ToArrayAsync(cancellationToken);

    public async Task<bool> TrySaveAsync(
        Guid tenantId,
        IReadOnlyCollection<Role> roles,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        dbContext.Roles.AddRange(roles);
        dbContext.RoleSeeds.Add(new RoleSeed { TenantId = tenantId, SeededAt = seededAt });
        try
        {
            // Un solo SaveChanges: EF lo envuelve en una transacción, así que quedan los roles y la marca o nada.
            // Dos siembras a la vez no se bloquean entre sí en círculo porque EF ordena estos inserts por nombre de tabla:
            // authorization.role_seeds va antes que authorization.roles, así que la segunda espera en PK_role_seeds sin tener
            // ningún otro candado. Si renombras cualquiera de las dos tablas, ese orden tiene que seguir igual.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Por nombre de restricción y no sólo por 23505: la marca del tenant, o una clave que la otra siembra ya
        // insertó. Cualquier otra violación es un error de verdad y sube.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_role_seeds" or "IX_roles_tenant_key",
        })
        {
            return false;
        }
        finally
        {
            // Falle como falle, lo que no se guardó seguiría en el ChangeTracker y se colaría en el próximo guardado; y lo
            // guardado se acumularía tenant tras tenant en la siembra del arranque.
            dbContext.ChangeTracker.Clear();
        }
    }
}
