using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>Tenancy → Store: quién llama y cómo se llaman los miembros que convirtieron o descartaron solicitudes.</summary>
internal sealed class StoreMemberDirectory(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    TenancyDbContext tenancy) : IStoreMemberDirectory
{
    // El mismo rótulo de LoansMemberDirectory y del panel de Personas.
    private const string NoName = "Sin nombre";

    public async Task<(Guid MembershipId, Guid UserId)> ResolveCallerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var userId = executionContext.SubjectId;
        var membershipId = await memberships.FindActiveMembershipIdAsync(userId, tenantId, cancellationToken)
            ?? throw new RequestForbiddenException(
                "authorization.denied", "The subject has no active membership in this tenant.");
        return (membershipId, userId);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var tenant = new TenantId(tenantId);
        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var found = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .ToListAsync(cancellationToken);
        return found.ToDictionary(
            membership => membership.Id.Value,
            membership => string.IsNullOrWhiteSpace(membership.DisplayName) ? NoName : membership.DisplayName);
    }

    public async Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var id = new MembershipId(membershipId);
        return await tenancy.Memberships.AsNoTracking()
            .AnyAsync(
                membership => membership.Id == id
                    && membership.TenantId == tenant
                    && membership.State == MembershipState.Active,
                cancellationToken);
    }

    public async Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var owner = await tenancy.Tenants.AsNoTracking()
            .Where(row => row.Id == tenant)
            .Select(row => (MembershipId?)row.OwnerMembershipId)
            .SingleOrDefaultAsync(cancellationToken);
        return owner?.Value
            ?? throw new InvalidOperationException($"The tenant {tenantId} does not exist, so it has no owner membership.");
    }
}
