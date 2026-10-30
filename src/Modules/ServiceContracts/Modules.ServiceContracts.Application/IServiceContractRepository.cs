using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

public interface IServiceContractRepository
{
    void Add(ServiceContract service);

    Task<ServiceContract?> FindAsync(Guid tenantId, ServiceContractId id, CancellationToken cancellationToken);

    /// <summary>Los servicios del tenant con esos ids, en un solo viaje: la lista de cobros pinta el nombre del servicio de cada fila.</summary>
    Task<IReadOnlyList<ServiceContract>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<ServiceContractId> ids, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ServiceContract> Items, int Total)> ListAsync(
        ServiceContractQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Los servicios que cumplen los filtros de <paramref name="query"/> sin paginar (se ignoran <c>Page</c> y <c>PageSize</c>), del tenant. El orden por
    /// nombre del cliente que pide el spec §4 lo hace el handler con <c>IServiceContractsCustomerLookup</c> y pagina en memoria: el nombre del cliente no está en este esquema.
    /// </summary>
    Task<IReadOnlyList<ServiceContract>> ListAllAsync(ServiceContractQuery query, CancellationToken cancellationToken);

    /// <summary>Los servicios <c>Active</c> del tenant: sobre ellos corre la generación de cobros del worker (§3.3, paso 3).</summary>
    Task<IReadOnlyList<ServiceContract>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Los servicios <c>Active</c> o <c>Paused</c> de esos clientes del tenant: la importación rechaza un nombre repetido (§3.2b, <c>service_exists</c>).</summary>
    Task<IReadOnlyList<ServiceContract>> ListOpenByCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}
