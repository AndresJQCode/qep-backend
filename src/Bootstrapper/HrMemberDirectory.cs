using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Las membresías que Hr puede nombrar: la persona de un empleado y el actor de su historial de acciones. Calco de
/// <see cref="LoansMemberDirectory"/> sin el permiso (cualquier miembro activo puede ser un empleado), más la búsqueda por el
/// usuario de la membresía. Sin caché; toda lectura va acotada al tenant.
/// </summary>
internal sealed class HrMemberDirectory(TenancyDbContext tenancy, IRolePermissionChecker permissions) : IHrMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas.
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<HrMemberRef>> FindAsync(
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
        return memberships.Select(ToRef).ToList();
    }

    public async Task<IReadOnlyList<HrMemberRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        // Orden estable: es la lista de GET /members-available.
        return active
            .Select(ToRef)
            .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .ToList();
    }

    public async Task<IReadOnlyList<HrMemberRef>> ListWithPermissionAsync(
        Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        // Authorization is asked only for whoever is active: a suspended or removed membership manages nothing, even if its role
        // grants the permission. Same pattern as ServiceContractsMemberDirectory.
        var granted = new List<HrMemberRef>();
        foreach (var membership in active)
        {
            if (await permissions.AnyGrantsAsync(tenant, membership.Roles, permission, cancellationToken))
            {
                granted.Add(ToRef(membership));
            }
        }

        return granted
            .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .ToList();
    }

    public async Task<HrMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var membership = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.UserId == subjectId)
            .OrderBy(candidate => candidate.State)
            .FirstOrDefaultAsync(cancellationToken);
        return membership is null ? null : ToRef(membership);
    }

    private static HrMemberRef ToRef(Membership membership) => new(
        membership.Id.Value,
        string.IsNullOrWhiteSpace(membership.DisplayName) ? NoName : membership.DisplayName,
        membership.State == MembershipState.Active);
}
