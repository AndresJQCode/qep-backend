using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record ListCustomerPriceListsQuery(Guid TenantId, Guid CustomerId)
    : IQuery<IReadOnlyList<CustomerPriceListDto>>;

public sealed class ListCustomerPriceListsHandler(
    ICustomerRepository customerRepository,
    ICustomerPriceListRepository priceListRepository,
    ICustomerPriceListLookup priceListLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomerPriceListsQuery, IReadOnlyList<CustomerPriceListDto>>
{
    public async Task<IReadOnlyList<CustomerPriceListDto>> HandleAsync(
        ListCustomerPriceListsQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.CustomerRead);

        var customerId = new CustomerId(query.CustomerId);
        var customer = await customerRepository.FindAsync(
            query.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(query.CustomerId);

        var assignments = await priceListRepository.ListAsync(
            query.TenantId, customer.Id, cancellationToken);
        if (assignments.Count == 0)
        {
            return [];
        }

        var priceListIds = assignments.Select(assignment => assignment.PriceListId).ToArray();
        var priceLists = await priceListLookup.ListByIdsAsync(priceListIds, cancellationToken);

        // Una asignacion cuya lista ya no se puede resolver (borrada por fuera de la regla que
        // DeletePriceList impone) simplemente no aparece — mismo criterio que ProductMapping con
        // una imagen que desaparecio de Storage.
        return assignments
            .Where(assignment => priceLists.ContainsKey(assignment.PriceListId))
            .Select(assignment =>
            {
                var priceList = priceLists[assignment.PriceListId];
                return new CustomerPriceListDto(
                    priceList.Id, priceList.Name, priceList.Prefix, priceList.IsActive);
            })
            .ToArray();
    }
}
