using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record GetCustomerQuery(Guid TenantId, Guid CustomerId) : IQuery<CustomerDto>;

public sealed class GetCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    IExecutionContext executionContext)
    : IQueryHandler<GetCustomerQuery, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        GetCustomerQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.CustomerRead);

        var customer = await repository.FindAsync(
            query.TenantId, new CustomerId(query.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(query.CustomerId);

        return await customer.ToDtoAsync(geographyLookup, classificationRepository, cancellationToken);
    }
}
