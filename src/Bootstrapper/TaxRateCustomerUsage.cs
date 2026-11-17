using Modules.Catalog.Application;
using Modules.Customers.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de clientes de <c>Customers</c> al puerto con el que <c>catalog</c> sabe si una tasa de retención
/// está asignada a algún cliente. No decide nada: sólo traduce.
/// </summary>
internal sealed class TaxRateCustomerUsage(ICustomerRepository repository) : ITaxRateCustomerUsage
{
    public Task<bool> AnyCustomerWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken) =>
        repository.AnyWithTaxRateAsync(tenantId, taxRateId, cancellationToken);
}
