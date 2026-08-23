using Modules.Customers.Domain;

namespace Modules.Customers.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
public interface ICustomerPriceListRepository
{
    Task<IReadOnlyList<CustomerPriceList>> ListAsync(
        Guid tenantId,
        CustomerId customerId,
        CancellationToken cancellationToken);

    void Add(CustomerPriceList assignment);

    void Remove(CustomerPriceList assignment);

    /// <summary>
    /// Si algún cliente del tenant tiene asignada esta lista de precios. La usa
    /// <c>IPriceListUsageLookup</c> (adaptado en Bootstrapper) para que <c>DeletePriceList</c>
    /// responda un 422 legible antes de dejar asignaciones huérfanas.
    /// </summary>
    Task<bool> AnyWithPriceListAsync(
        Guid tenantId,
        Guid priceListId,
        CancellationToken cancellationToken);
}
