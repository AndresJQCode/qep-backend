using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Un empleado «tiene contrato con la empresa» si aparece en el directorio de Hr con cualquier rango de fechas.</summary>
public sealed class PayrollEmployees(IPayrollHrDirectory hr)
{
    public async Task RequireInCompanyAsync(Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken)
    {
        var employees = await hr.ListEmployeesAsync(tenantId, companyId, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        if (employees.All(employee => employee.EmployeeId != employeeId))
        {
            throw PayrollNotFound.Employee(employeeId);
        }
    }
}

public sealed record ListRecurringConceptsQuery(Guid TenantId, Guid CompanyId, Guid EmployeeId)
    : IQuery<IReadOnlyList<RecurringConceptDto>>;

/// <summary>
/// Los recurrentes de un empleado en la empresa, con su acumulado y su estado de hoy. Los puede leer quien tenga
/// <c>payroll.period.read</c> o <c>payroll.settings.manage</c> (decisión del owner).
/// </summary>
public sealed class ListRecurringConceptsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    PayrollCompanies companies,
    PayrollEmployees employees,
    RecurringConceptComposer composer)
    : IQueryHandler<ListRecurringConceptsQuery, IReadOnlyList<RecurringConceptDto>>
{
    public async Task<IReadOnlyList<RecurringConceptDto>> HandleAsync(
        ListRecurringConceptsQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAnyAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.SettingsManage);
        await companies.RequireAsync(query.TenantId, query.CompanyId, cancellationToken);
        await employees.RequireInCompanyAsync(query.TenantId, query.CompanyId, query.EmployeeId, cancellationToken);

        return await composer.ListAsync(query.TenantId, query.CompanyId, query.EmployeeId, cancellationToken);
    }
}
