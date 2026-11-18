using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record GetDistributorQuery(Guid TenantId, Guid DistributorId) : IQuery<DistributorDetailDto>;

public sealed class GetDistributorHandler(
    IExecutionContext executionContext,
    IDistributorRepository distributors,
    DistributorComposer composer)
    : IQueryHandler<GetDistributorQuery, DistributorDetailDto>
{
    public async Task<DistributorDetailDto> HandleAsync(GetDistributorQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.DistributorRead);
        var distributor = await distributors.GetAsync(query.TenantId, new DistributorId(query.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(query.DistributorId);

        return await composer.DetailAsync(distributor, cancellationToken);
    }
}
