using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record GetClientClassificationQuery(Guid TenantId, Guid ClassificationId)
    : IQuery<ClientClassificationDto>;

public sealed class GetClientClassificationHandler(
    IClientClassificationRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetClientClassificationQuery, ClientClassificationDto>
{
    public async Task<ClientClassificationDto> HandleAsync(
        GetClientClassificationQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.ClassificationRead);

        var classification = await repository.FindAsync(
            query.TenantId, new ClientClassificationId(query.ClassificationId), cancellationToken);

        return classification is null
            ? throw ClientClassificationNotFound.For(query.ClassificationId)
            : classification.ToDto();
    }
}
