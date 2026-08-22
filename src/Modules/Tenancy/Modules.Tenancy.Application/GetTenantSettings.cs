using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record GetTenantSettingsQuery(TenantId TenantId) : IQuery<TenantSettingsDto>;

public sealed class GetTenantSettingsHandler(
    ITenantRepository tenantRepository,
    IExecutionContext executionContext)
    : IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        GetTenantSettingsQuery query,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(query.TenantId);
        var tenant = await tenantRepository.GetAsync(query.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found",
                "Tenant settings were not found.");

        return tenant.ToSettingsDto();
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read tenant settings.");
        }
    }
}
