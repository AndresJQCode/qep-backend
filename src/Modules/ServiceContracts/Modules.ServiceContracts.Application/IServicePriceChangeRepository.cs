using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

public interface IServicePriceChangeRepository
{
    void Add(ServicePriceChange change);

    /// <summary>El historial de precios de un contrato, del más reciente al más viejo, para el detalle.</summary>
    Task<IReadOnlyList<ServicePriceChange>> ListByServiceAsync(ServiceContractId serviceId, CancellationToken cancellationToken);
}
