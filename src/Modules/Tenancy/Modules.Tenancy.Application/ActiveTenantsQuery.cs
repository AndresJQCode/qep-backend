namespace Modules.Tenancy.Application;

public sealed class ActiveTenantsQuery(IMembershipRepository membershipRepository)
    : IActiveTenantsQuery
{
    public async Task<IReadOnlyCollection<ActiveTenantSummary>> ListActiveTenantsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await membershipRepository.ListActiveTenantSummariesByUserAsync(userId, cancellationToken);
}
