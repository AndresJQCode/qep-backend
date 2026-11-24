using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Payroll.Application;
using Modules.Payroll.Domain.Engine;

namespace Bootstrapper;

/// <summary>
/// Adapta Hr (empresas, empleados, contratos y su configuración) al puerto de Payroll (<c>PayrollLayerTests.NoPayrollLayerReferencesHr</c>).
/// Los enums de Hr viajan con su <b>nombre</b>; la clase de riesgo se traduce por nombre al <see cref="ArlClass"/> de Payroll. Toda
/// lectura va acotada al tenant: otro tenant con el mismo <c>companyId</c> no ve nada.
/// </summary>
internal sealed class PayrollHrDirectory(
    IHrCompanyLookup companies,
    IEmployeeRepository employees,
    IHrSettingsRepository settings) : IPayrollHrDirectory
{
    public async Task<IReadOnlyList<PayrollCompanyRef>> ListActiveCompaniesAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.ListActiveAsync(tenantId, cancellationToken)).Select(ToRef).ToList();

    public async Task<PayrollCompanyRef?> FindCompanyAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, companyId, cancellationToken) is { } company ? ToRef(company) : null;

    public async Task<IReadOnlyList<HrEmployeeSource>> ListEmployeesAsync(
        Guid tenantId, Guid companyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
    {
        var found = await employees.ListForPayrollAsync(tenantId, companyId, periodStart, periodEnd, cancellationToken);
        if (found.Count == 0)
        {
            return [];
        }

        // Todo el catálogo, archivado incluido: un cargo o un centro archivado después sigue nombrándose en el historial.
        var snapshot = await settings.ListAsync(tenantId, cancellationToken);
        var positions = snapshot.Positions.ToDictionary(position => position.Id);
        var sites = snapshot.Sites.ToDictionary(site => site.Id);
        var workplaces = snapshot.Workplaces.ToDictionary(workplace => workplace.Id);

        return found.Select(employee => new HrEmployeeSource(
                employee.Id.Value,
                employee.FirstName,
                employee.LastName,
                employee.DocumentType.ToString(),
                employee.DocumentNumber,
                employee.ContributorSubtype.ToString(),
                employee.ForeignPensionExempt,
                employee.Contracts
                    .Where(contract => contract.CompanyId == companyId
                        && contract.StartDate <= periodEnd
                        && (contract.EndedOn is null || contract.EndedOn >= periodStart))
                    .Select(contract => ToSource(contract, positions, sites, workplaces))
                    .ToList(),
                employee.RestDays.ToList()))
            .ToList();
    }

    private static HrContractSource ToSource(
        EmploymentContract contract,
        IReadOnlyDictionary<PositionId, Position> positions,
        IReadOnlyDictionary<SiteId, Site> sites,
        IReadOnlyDictionary<WorkplaceId, Workplace> workplaces) => new(
        contract.Id.Value,
        contract.ContractType.ToString(),
        contract.SalaryType.ToString(),
        contract.StartDate,
        contract.EndedOn,
        contract.Salaries
            .OrderBy(salary => salary.EffectiveDate)
            .Select(salary => new HrSalarySource(salary.EffectiveDate, salary.Amount, salary.Teleworker))
            .ToList(),
        contract.Assignments
            .OrderBy(assignment => assignment.EffectiveDate)
            .Select(assignment =>
            {
                var workplace = workplaces.GetValueOrDefault(assignment.WorkplaceId);
                var site = sites.GetValueOrDefault(assignment.SiteId);
                return new HrAssignmentSource(
                    assignment.EffectiveDate,
                    positions.GetValueOrDefault(assignment.PositionId)?.Name ?? string.Empty,
                    site?.Name ?? string.Empty,
                    workplace?.Name ?? string.Empty,
                    workplace is null ? null : Enum.Parse<ArlClass>(workplace.RiskClass.ToString()),
                    workplace?.HighRiskPension ?? false,
                    site?.CompensationFund);
            })
            .ToList());

    private static PayrollCompanyRef ToRef(HrCompanyRef company) =>
        new(company.Id, company.Name, company.TaxId, company.IsActive);
}
