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

    public async Task<IReadOnlyList<ActiveTenantSummary>> ListActiveTenantSummariesByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await (
            from membership in dbContext.Memberships
            join tenant in dbContext.Tenants on membership.TenantId equals tenant.Id
            where membership.UserId == userId && membership.State == MembershipState.Active
            orderby tenant.DisplayName, tenant.Id
            // EF.Property y no membership.Roles: el mapeo ignora la propiedad y le da la columna
            // al campo privado (TenancyDbContext.cs:91-93).
            select new ActiveTenantSummary(
                tenant.Id.Value,
                tenant.DisplayName,
                EF.Property<List<string>>(membership, "_roles")))
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

    public Task<Membership?> FindByInvitationTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken) =>
        dbContext.Memberships.SingleOrDefaultAsync(
            membership => membership.InvitationTokenHash == tokenHash,
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

    public Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken)
    {
        // Sin filtro por estado a propósito (D4): una quitada conserva su código y lo sigue
        // bloqueando, igual que el índice.
        var query = dbContext.Memberships.Where(membership =>
            membership.TenantId == tenantId && membership.AdvisorCode == advisorCode);
        if (exceptMembershipId is { } except)
        {
            query = query.Where(membership => membership.Id != except);
        }

        return query.AnyAsync(cancellationToken);
    }

    public void Add(Membership membership) => dbContext.Memberships.Add(membership);
}
