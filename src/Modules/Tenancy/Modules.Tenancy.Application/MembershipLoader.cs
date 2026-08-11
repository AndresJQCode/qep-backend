using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class MembershipLoader
{
    // Carga una membresía acotada al tenant. Un tenant que no coincide se reporta como
    // no encontrado, para nunca filtrar la existencia de membresías entre tenants.
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
