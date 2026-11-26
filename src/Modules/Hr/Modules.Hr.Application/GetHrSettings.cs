using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record GetHrSettingsQuery(Guid TenantId) : IQuery<HrSettingsDto>;

public sealed class GetHrSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    HrSettingsComposer composer)
    : IQueryHandler<GetHrSettingsQuery, HrSettingsDto>
{
    public async Task<HrSettingsDto> HandleAsync(GetHrSettingsQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead);

        return await composer.ComposeAllAsync(query.TenantId, cancellationToken);
    }
}
