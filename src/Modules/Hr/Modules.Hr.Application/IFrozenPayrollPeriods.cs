namespace Modules.Hr.Application;

/// <summary>Una nómina congelada (<c>Approved</c> o <c>Closed</c>) con resultado guardado del empleado: empresa, inicio y fin.</summary>
public sealed record FrozenPayrollPeriod(Guid CompanyId, DateOnly StartDate, DateOnly EndDate);

/// <summary>
/// Las nóminas congeladas que ya usaron los registros de cargo y lugar de un empleado (spec 2026-11-26, «Nómina»). Hr no referencia
/// a Payroll: lo implementa un adaptador del Bootstrapper sobre el puerto de lectura de Payroll, como <see cref="ILegalParameterUsage"/>.
/// </summary>
public interface IFrozenPayrollPeriods
{
    Task<IReadOnlyList<FrozenPayrollPeriod>> ListForEmployeeAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken);
}
