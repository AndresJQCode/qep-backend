using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record ListVacationsQuery(Guid TenantId, Guid CompanyId, Guid EmployeeId) : IQuery<IReadOnlyList<VacationDto>>;

/// <summary>Las vacaciones de un empleado en la empresa, las más recientes primero. Las lee quien puede leer períodos.</summary>
public sealed class ListVacationsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    PayrollCompanies companies,
    PayrollEmployees employees,
    VacationComposer composer)
    : IQueryHandler<ListVacationsQuery, IReadOnlyList<VacationDto>>
{
    public async Task<IReadOnlyList<VacationDto>> HandleAsync(ListVacationsQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, PayrollPermissions.PeriodRead);
        await companies.RequireAsync(query.TenantId, query.CompanyId, cancellationToken);
        await employees.RequireInCompanyAsync(query.TenantId, query.CompanyId, query.EmployeeId, cancellationToken);

        return await composer.ListAsync(query.TenantId, query.CompanyId, query.EmployeeId, cancellationToken);
    }
}
