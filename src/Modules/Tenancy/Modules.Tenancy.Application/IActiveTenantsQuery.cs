namespace Modules.Tenancy.Application;

/// <summary>
/// Read-only lookup of a user's active tenant memberships, published for the
/// <c>GET /auth/me</c> session-revalidation endpoint to rebuild the same shape the
/// login flow returns, without mutating anything.
/// </summary>
public interface IActiveTenantsQuery
{
    Task<IReadOnlyCollection<Guid>> ListActiveTenantIdsAsync(
        Guid userId,
        CancellationToken cancellationToken);
}
