using Modules.Billing.Domain;

namespace Modules.Billing.Application;

public interface ICustomerBillingProfileRepository
{
    Task<CustomerBillingProfile?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Todos los perfiles del tenant: el worker necesita canal y encargado de cada cliente con cobros.</summary>
    Task<IReadOnlyList<CustomerBillingProfile>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(CustomerBillingProfile profile);
}
