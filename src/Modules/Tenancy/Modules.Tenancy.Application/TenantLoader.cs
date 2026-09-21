using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class TenantLoader
{
    // Los casos de uso de membresías cargan el tenant por dos motivos, y los dos son el mismo:
    // sólo él sabe quién es su owner (Tenant.OwnerMembershipId). Lo necesitan para ejercer la
    // guarda del agregado y para poder decir `IsOwner` en la respuesta.
    public static async Task<Tenant> LoadAsync(
        ITenantRepository repository,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        await repository.GetAsync(tenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found",
                "The tenant was not found.");
}
