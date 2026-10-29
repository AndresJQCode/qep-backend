using Modules.Billing.Domain;

namespace Modules.Billing.Application;

public interface IBillingServiceRepository
{
    void Add(BillingService service);

    Task<BillingService?> FindAsync(Guid tenantId, BillingServiceId id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<BillingService> Items, int Total)> ListAsync(
        BillingServiceQuery query, CancellationToken cancellationToken);

    /// <summary>Los servicios <c>Active</c> del tenant: sobre ellos corre la generación de cobros del worker (§3.3, paso 3).</summary>
    Task<IReadOnlyList<BillingService>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
