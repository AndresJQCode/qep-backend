namespace Modules.Tenancy.Application;

public sealed class ActiveTenantsQuery(IMembershipRepository membershipRepository)
    : IActiveTenantsQuery
{
    public async Task<IReadOnlyCollection<Guid>> ListActiveTenantIdsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var tenants = await membershipRepository.ListActiveTenantsByUserAsync(
            userId,
            cancellationToken);
        return tenants.Select(tenant => tenant.Value).ToArray();
    }
}
