using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record ListClientClassificationsQuery(Guid TenantId)
    : IQuery<IReadOnlyList<ClientClassificationDto>>;

public sealed class ListClientClassificationsHandler(
    IClientClassificationRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListClientClassificationsQuery, IReadOnlyList<ClientClassificationDto>>
{
    public async Task<IReadOnlyList<ClientClassificationDto>> HandleAsync(
        ListClientClassificationsQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.ClassificationRead);

        var classifications = await repository.ListAsync(query.TenantId, cancellationToken);

        return classifications.Select(classification => classification.ToDto()).ToArray();
    }
}
