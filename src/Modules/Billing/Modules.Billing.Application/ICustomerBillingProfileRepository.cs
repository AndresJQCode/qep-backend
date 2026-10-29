using Modules.Billing.Domain;

namespace Modules.Billing.Application;

public interface ICustomerBillingProfileRepository
{
    Task<CustomerBillingProfile?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Todos los perfiles del tenant: el worker necesita canal y encargado de cada cliente con cobros.</summary>
    Task<IReadOnlyList<CustomerBillingProfile>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Los perfiles de esos clientes en un solo viaje: el encargado de cada cobro de una página, sin una consulta por fila.</summary>
    Task<IReadOnlyList<CustomerBillingProfile>> ListByCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    void Add(CustomerBillingProfile profile);
}
