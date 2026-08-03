using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class MembershipLoader
{
    // Loads a membership scoped to the tenant. A mismatched tenant is reported as
    // not found so membership existence is never leaked across tenants.
    public static async Task<Membership> LoadAsync(
        IMembershipRepository repository,
        MembershipId id,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        await repository.FindByIdAsync(id, tenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.membership.not_found",
                "The membership was not found.");
}
