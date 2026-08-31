using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class MembershipRepository(TenancyDbContext dbContext) : IMembershipRepository
{
    public Task<Membership?> FindByUserAndTenantAsync(
        Guid userId,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        dbContext.Memberships.SingleOrDefaultAsync(
            membership => membership.UserId == userId && membership.TenantId == tenantId,
            cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .Where(membership =>
                membership.UserId == userId &&
                membership.State == MembershipState.Invited)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .Where(membership =>
                membership.UserId == userId &&
                membership.State == MembershipState.Active)
            .Select(membership => membership.TenantId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .Where(membership => membership.UserId == userId)
            .ToListAsync(cancellationToken);

    public Task<Membership?> FindByIdAsync(
        MembershipId id,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        dbContext.Memberships.SingleOrDefaultAsync(
            membership => membership.Id == id && membership.TenantId == tenantId,
            cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .Where(membership => membership.TenantId == tenantId)
            .OrderByDescending(membership => membership.InvitedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
        TenantId tenantId,
        MembershipId excludeId,
        CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .Where(membership =>
                membership.TenantId == tenantId &&
                membership.Id != excludeId &&
                membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

    public void Add(Membership membership) => dbContext.Memberships.Add(membership);
}
