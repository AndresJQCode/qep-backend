using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

public sealed record PayrollCompanyRef(Guid Id, string Name, string TaxId, bool IsActive);

public sealed record HrSalarySource(DateOnly EffectiveDate, decimal Amount, bool Teleworker);

/// <param name="CompensationFund">La caja de la sede (O-6): sólo se muestra, no cambia el cálculo.</param>
public sealed record HrAssignmentSource(
    DateOnly EffectiveDate,
    string PositionName,
    string SiteName,
    string WorkplaceName,
    ArlClass? RiskClass,
    bool HighRiskPension,
    string? CompensationFund);

/// <summary>Un contrato con todo su historial de salario y de cargo y lugar. Los tipos de Hr viajan con el <b>nombre</b> de su enum.</summary>
public sealed record HrContractSource(
    Guid ContractId,
    string ContractType,
    string SalaryType,
    DateOnly StartDate,
    DateOnly? EndedOn,
    IReadOnlyList<HrSalarySource> Salaries,
    IReadOnlyList<HrAssignmentSource> Assignments);

/// <param name="RestDays">Los días de descanso del empleado en Hr (siempre al menos uno): deciden cuáles son sus días hábiles de vacaciones.</param>
public sealed record HrEmployeeSource(
    Guid EmployeeId,
    string FirstName,
    string LastName,
    string DocumentType,
    string DocumentNumber,
    string ContributorSubtype,
    bool ForeignPensionExempt,
    IReadOnlyList<HrContractSource> Contracts,
    IReadOnlyList<DayOfWeek> RestDays);

/// <summary>
/// El puerto por el que Payroll lee empresas, empleados y contratos de Hr (<c>PayrollLayerTests.NoPayrollLayerReferencesHr</c>): el
/// adaptador vive en Bootstrapper. Payroll no conoce los enums de Hr; recibe sus nombres como texto.
/// </summary>
public interface IPayrollHrDirectory
{
    Task<IReadOnlyList<PayrollCompanyRef>> ListActiveCompaniesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Activa o inactiva; nulo si no existe en el tenant.</summary>
    Task<PayrollCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    /// <summary>Sólo los contratos de <paramref name="companyId"/> que cubren algún día de [periodStart, periodEnd]; sin uno, el empleado no sale.</summary>
    Task<IReadOnlyList<HrEmployeeSource>> ListEmployeesAsync(
        Guid tenantId, Guid companyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken);
}
