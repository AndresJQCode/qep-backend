using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public interface ITenantRepository
{
    Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>
    /// Todos los ids de tenant existentes. La usa <c>pricing</c> para sembrar sus listas de
    /// precio por defecto en cada tenant al arrancar — el único consumidor hoy, así que trae sólo
    /// el id y no el agregado completo.
    /// </summary>
    Task<IReadOnlyList<TenantId>> ListAllIdsAsync(CancellationToken cancellationToken);

    void Add(Tenant tenant);
}
