using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record GetOperatorTenantQuery(TenantId TenantId, TenantId TargetTenantId) : IQuery<OperatorTenantDetailDto>;

public sealed class GetOperatorTenantHandler(
    IOperatorTenantReader reader,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetOperatorTenantQuery, OperatorTenantDetailDto>
{
    public Task<OperatorTenantDetailDto> HandleAsync(GetOperatorTenantQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        // D7: aquí el 404 sí es correcto; el operador ve todos los tenants por diseño.
        return OperatorTenantDetails.LoadAsync(reader, operatorTenant, query.TargetTenantId, cancellationToken);
    }
}
