using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class MembershipRoleUsage(TenancyDbContext dbContext) : IMembershipRoleUsage
{
    /// <summary>
    /// Los conjuntos de roles de las membresías <b>activas</b> del tenant.
    /// </summary>
    /// <remarks>
    /// Sólo activas: una membresía suspendida o removida no autoriza nada, así que ni retiene
    /// un rol de ser borrado ni cuenta como "alguien que todavía administra". Contarlas dejaría
    /// al tenant creyendo que hay un administrador que en realidad no puede entrar — que es
    /// exactamente el lockout que estas guardas existen para evitar.
    ///
    /// `AsNoTracking` porque nadie modifica estas entidades: se leen para decidir.
    /// </remarks>
    public async Task<IReadOnlyCollection<IReadOnlyCollection<string>>> ActiveRoleSetsAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var memberships = await dbContext.Memberships
            .AsNoTracking()
            .Where(membership =>
                membership.TenantId == tenantId &&
                membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        return memberships
            .Select(membership => membership.Roles)
            .ToArray();
    }
}
