using Modules.Billing.Domain;

namespace Modules.Billing.Application;

public interface IBillingServiceRepository
{
    void Add(BillingService service);

    Task<BillingService?> FindAsync(Guid tenantId, BillingServiceId id, CancellationToken cancellationToken);

    /// <summary>Los servicios del tenant con esos ids, en un solo viaje: la lista de cobros pinta el nombre del servicio de cada fila.</summary>
    Task<IReadOnlyList<BillingService>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<BillingServiceId> ids, CancellationToken cancellationToken);

    Task<(IReadOnlyList<BillingService> Items, int Total)> ListAsync(
        BillingServiceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Los servicios que cumplen los filtros de <paramref name="query"/> sin paginar (se ignoran <c>Page</c> y <c>PageSize</c>), del tenant. El orden por
    /// nombre del cliente que pide el spec §4 lo hace el handler con <c>IBillingCustomerLookup</c> y pagina en memoria: el nombre del cliente no está en este esquema.
    /// </summary>
    Task<IReadOnlyList<BillingService>> ListAllAsync(BillingServiceQuery query, CancellationToken cancellationToken);

    /// <summary>Los servicios <c>Active</c> del tenant: sobre ellos corre la generación de cobros del worker (§3.3, paso 3).</summary>
    Task<IReadOnlyList<BillingService>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
