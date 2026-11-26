using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record ListPayrollCompaniesQuery(Guid TenantId) : IQuery<IReadOnlyList<PayrollCompanyDto>>;

/// <summary>
/// Las empresas activas más las inactivas que tienen configuración guardada o algún período (M-1): una empresa desactivada después
/// de liquidar o de configurarse sigue en la lista, con <c>isActive: false</c>, para poder ver sus períodos. Basta uno de los dos permisos de
/// lectura (<see cref="PayrollPolicies.SettingsView"/>).
/// </summary>
public sealed class ListPayrollCompaniesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPayrollHrDirectory directory,
    ICompanyPayrollSettingsRepository settings,
    IPayrollPeriodRepository periods)
    : IQueryHandler<ListPayrollCompaniesQuery, IReadOnlyList<PayrollCompanyDto>>
{
    public async Task<IReadOnlyList<PayrollCompanyDto>> HandleAsync(
        ListPayrollCompaniesQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAnyAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.SettingsManage);

        var companies = (await directory.ListActiveCompaniesAsync(query.TenantId, cancellationToken)).ToDictionary(company => company.Id);
        var withData = (await settings.ListCompanyIdsAsync(query.TenantId, cancellationToken))
            .Concat(await periods.ListCompanyIdsAsync(query.TenantId, cancellationToken))
            .Distinct();
        foreach (var companyId in withData)
        {
            if (!companies.ContainsKey(companyId)
                && await directory.FindCompanyAsync(query.TenantId, companyId, cancellationToken) is { } inactive)
            {
                companies[companyId] = inactive;
            }
        }

        return companies.Values
            .OrderBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(company => company.Id)
            .Select(PayrollCompanies.ToDto)
            .ToList();
    }
}
