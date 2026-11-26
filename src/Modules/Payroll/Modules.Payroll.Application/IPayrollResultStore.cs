using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>El resultado guardado de un empleado en un período: la última liquidación, con su huella y lo que aplicó de cada recurrente.</summary>
/// <param name="RecurringApplied">Por <c>recurringId</c>: lo que cada concepto recurrente aplicó (R-12).</param>
public sealed record EmployeeResult(
    Guid EmployeeId,
    string DocumentType,
    string DocumentNumber,
    string FullName,
    string? Position,
    string? Workplace,
    EmployeeLiquidation Liquidation,
    string Fingerprint,
    IReadOnlyDictionary<Guid, decimal> RecurringApplied);

/// <summary>Los resultados de la última liquidación de cada período (<c>payroll.employee_results</c>). Se llenan en la Tarea 11.</summary>
public interface IPayrollResultStore
{
    /// <summary>Sin seguimiento, por nombre. Vacío si el período no es del tenant o nunca se liquidó.</summary>
    Task<IReadOnlyList<EmployeeResult>> ListAsync(Guid tenantId, PayrollPeriodId periodId, CancellationToken cancellationToken);

    Task<EmployeeResult?> FindAsync(Guid tenantId, PayrollPeriodId periodId, Guid employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reemplaza todos los resultados del período por <paramref name="results"/> dentro de la misma unidad de trabajo: se guardan
    /// con el <c>SaveChanges</c> del caso de uso, junto con el período. Es asíncrono porque carga lo que ya hay para actualizarlo en
    /// su lugar (el índice único <c>(period_id, employee_id)</c> no se puede borrar y reinsertar en un mismo guardado).
    /// </summary>
    Task ReplaceAllAsync(
        Guid tenantId, PayrollPeriodId periodId, IReadOnlyList<EmployeeResult> results, CancellationToken cancellationToken);

    /// <summary>
    /// Lo aplicado por cada concepto recurrente de un empleado, sumado sobre los resultados de los períodos que no están en
    /// <c>Draft</c> de la empresa con <c>start_date &lt; before</c>, o todos si <paramref name="before"/> es nulo (R-12).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> SumRecurringAppliedAsync(
        Guid tenantId, Guid companyId, Guid employeeId, DateOnly? before, CancellationToken cancellationToken);
}
