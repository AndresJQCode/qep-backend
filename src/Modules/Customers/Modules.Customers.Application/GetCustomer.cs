using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record GetCustomerQuery(Guid TenantId, Guid CustomerId) : IQuery<CustomerDto>;

public sealed class GetCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
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

        var dto = await customer.ToDtoAsync(geographyLookup, classificationRepository, tagRepository, cancellationToken);
        if (customer.MergedIntoId is not { } survivorId)
        {
            return dto;
        }

        // La ficha de un fusionado se lee (§3.3) y dice a dónde fue. Un destino que no se encuentra no pasa: la fusión lo deja
        // en el mismo tenant y nada lo borra; si faltara, el campo queda en null y la ficha sigue leyéndose.
        var names = await repository.FindNamesByIdsAsync(query.TenantId, [survivorId], cancellationToken);
        return names.TryGetValue(survivorId, out var survivorName)
            ? dto with { MergedInto = new CustomerMergedIntoDto(survivorId.Value, survivorName) }
            : dto;
    }
}
