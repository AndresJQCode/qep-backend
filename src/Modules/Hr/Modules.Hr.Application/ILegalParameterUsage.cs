using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Puerto hacia las nóminas ya calculadas (Payroll): Hr no puede referenciar a Payroll, así que el adaptador vive en Bootstrapper.
/// Sirve a <see cref="LegalParameterUsageRule"/> para saber si tocar una fila de valor legal reescribiría la historia de una liquidación.
/// </summary>
public interface ILegalParameterUsage
{
    /// <summary>
    /// Un sello por período calculado, <b>de todos los tenants</b>: los valores legales son dato de la plataforma, no de un tenant.
    /// El nombre lo dice a propósito para que nadie lo use con un tenant en la cabeza.
    /// </summary>
    Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(CancellationToken cancellationToken);
}
