using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Los miembros que Loans puede nombrar como encargados. Membresía del tenant (Tenancy) + roles → permiso (Authorization, por
/// <see cref="IRolePermissionChecker"/>), sin caché: mismo patrón que <see cref="ServiceContractsMemberDirectory"/>, sin el correo
/// porque Loans no escribe a nadie.
///
/// Elegible como encargado es quien tiene la membresía activa y <c>loans.loan.manage</c>, no cualquier miembro: el encargado es
/// quien cobra las cuotas.
/// </summary>
internal sealed class LoansMemberDirectory(
    TenancyDbContext tenancy,
    IRolePermissionChecker permissions) : ILoansMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas (MembershipEndpoints.cs:336).
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<LoansMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return [];
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var tenant = new TenantId(tenantId);
        var memberships = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .ToListAsync(cancellationToken);

        var result = new List<LoansMemberRef>(memberships.Count);
        foreach (var membership in memberships)
        {
            result.Add(await ToRefAsync(tenant, membership, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<LoansMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        var managers = new List<LoansMemberRef>();
        foreach (var membership in active)
        {
            var member = await ToRefAsync(tenant, membership, cancellationToken);
            if (member.CanManage)
            {
                managers.Add(member);
            }
        }

        // Orden estable: es la lista de GET /assignees.
        return managers
            .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .ToArray();
    }

    private async Task<LoansMemberRef> ToRefAsync(TenantId tenant, Membership membership, CancellationToken cancellationToken)
    {
        var isActive = membership.State == MembershipState.Active;
        // Sólo se consulta a Authorization por quien está activo: una membresía suspendida o quitada no gestiona nada,
        // aunque su rol lo conceda.
        var canManage = isActive
            && await permissions.AnyGrantsAsync(tenant, membership.Roles, LoansPermissions.Manage, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(membership.DisplayName) ? NoName : membership.DisplayName;
        return new LoansMemberRef(membership.Id.Value, displayName, isActive, canManage);
    }
}