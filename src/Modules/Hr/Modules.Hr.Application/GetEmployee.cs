using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record GetEmployeeQuery(Guid TenantId, Guid EmployeeId) : IQuery<EmployeeDetailDto>;

public sealed class GetEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    EmployeeDetailComposer composer)
    : IQueryHandler<GetEmployeeQuery, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(GetEmployeeQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead);

        var employee = await employees.FindAsync(query.TenantId, new EmployeeId(query.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(query.EmployeeId);
        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), today, cancellationToken);
    }
}
