using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>El puerto por el que Payroll lee los valores legales (que son de Hr): un conjunto completo resuelto a una fecha.</summary>
public interface IPayrollLegalValues
{
    /// <summary>
    /// Resuelve todos los valores y los tramos a <paramref name="asOf"/> (el primer día del período). Si falta cualquier clave o no
    /// hay tramos lanza <c>PayrollDomainException</c> <c>payroll.legal_parameter.missing</c> y el mensaje nombra lo que falta.
    /// </summary>
    Task<LegalValueSet> ResolveAsync(DateOnly asOf, CancellationToken cancellationToken);
}
