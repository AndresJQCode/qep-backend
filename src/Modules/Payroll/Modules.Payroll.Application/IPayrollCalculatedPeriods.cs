namespace Modules.Payroll.Application;

/// <summary>
/// Un período de nómina ya calculado, visto desde fuera de Payroll: la fecha con que se resolvieron sus valores legales
/// (<c>legal_values_date</c>: el 1 o el 16 según la quincena) y la empresa. No lleva tenant, nómina ni nombres: quien lo lee sólo
/// cuenta períodos y empresas.
/// </summary>
public sealed record PayrollCalculatedPeriod(DateOnly LegalValuesDate, Guid CompanyId);

/// <summary>
/// El puerto de lectura de la consola de valores legales del operador (spec 2026-11-22, «Uso»): Hr no puede referenciar a Payroll, así
/// que el Bootstrapper traduce estos períodos al puerto de Hr.
/// </summary>
public interface IPayrollCalculatedPeriods
{
    /// <summary>
    /// Los períodos con <c>legal_values_date</c> no nula, uno por período, <b>de todos los tenants</b>: los valores legales son dato de la
    /// plataforma y <c>PayrollDbContext</c> no tiene filtro global de tenant, así que no hay nada que saltarse. El nombre lo dice a
    /// propósito para que nadie lo use con un tenant en la cabeza.
    /// </summary>
    Task<IReadOnlyList<PayrollCalculatedPeriod>> ListOfAllTenantsAsync(CancellationToken cancellationToken);
}
