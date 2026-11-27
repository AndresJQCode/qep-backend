namespace Modules.Payroll.Application;

/// <summary>Un período congelado (<c>Approved</c> o <c>Closed</c>) visto desde fuera de Payroll: empresa, inicio y fin.</summary>
public sealed record PayrollFrozenPeriod(Guid CompanyId, DateOnly StartDate, DateOnly EndDate);

/// <summary>
/// El puerto de lectura con que Hr sabe qué registros de cargo y lugar ya rigieron en una nómina congelada (spec 2026-11-26,
/// «Nómina»): Hr no puede referenciar a Payroll, así que el Bootstrapper traduce estos períodos al puerto de Hr.
/// </summary>
public interface IPayrollFrozenPeriods
{
    /// <summary>
    /// Los períodos del tenant en <c>Approved</c> o <c>Closed</c> que tienen resultado guardado del empleado. Un empleado excluido o
    /// dado de alta después no tiene resultado, así que esos períodos no salen. <c>Liquidated</c>, <c>Reopened</c> y <c>Draft</c>
    /// tampoco (decisión (a) del owner, 2026-11-26).
    /// </summary>
    Task<IReadOnlyList<PayrollFrozenPeriod>> ListWithResultOfAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken);
}
