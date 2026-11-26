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
    /// Sólo activas: una membresía suspendida o removida no autoriza nada, así que no cuenta
    /// como "alguien que todavía administra". Contarla dejaría al tenant creyendo que hay un
    /// administrador que en realidad no puede entrar — que es exactamente el lockout que la
    /// guarda existe para evitar. Qué retiene un rol ante su borrado es otra pregunta:
    /// <see cref="AnyHoldsRoleAsync"/>.
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

    /// <remarks>
    /// Los estados son los de <see cref="Membership.GrantsOrPromisesAccess"/>, repetidos porque EF no traduce esa
    /// propiedad. Una suspendida se puede reactivar y una invitada todavía se acepta o se le cambian los roles, así que
    /// las dos retienen el rol; quitada y vencida no, porque al reinvitar los roles se reemplazan.
    ///
    /// <c>AnyAsync</c>: la respuesta es sí o no, sin traer filas.
    /// </remarks>
    public Task<bool> AnyHoldsRoleAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken) =>
        dbContext.Memberships.AnyAsync(
            membership =>
                membership.TenantId == tenantId &&
                (membership.State == MembershipState.Invited ||
                    membership.State == MembershipState.Active ||
                    membership.State == MembershipState.Suspended) &&
                EF.Property<List<string>>(membership, "_roles").Contains(role),
            cancellationToken);
}
