namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Paso 8: salud y pensión del empleado, sobre el IBC del mes y descontando lo retenido en la primera quincena (R-5). Al trabajador se le
/// descuenta el 4 % al peso, como Siigo: la aproximación a la centena (Decreto 1990/2016) es de la cotización en la PILA, que asume el
/// empleador, no del descuento de nómina (decisión del owner, 2026-11-23).
/// </summary>
public static class EmployeeDeductionsRule
{
    private const string Formula = "al peso(IBC del mes × tasa) − lo retenido en la primera quincena";

    public static IReadOnlyList<PayrollLine> Compute(EngineInput input, IbcOutcome ibc) =>
    [
        Line("health_employee", "Salud", ibc, input.Legal.HealthEmployeeRate, input.Prior.HealthEmployee),
        Line("pension_employee", "Pensión", ibc, input.Legal.PensionEmployeeRate, input.Prior.PensionEmployee),
    ];

    private static PayrollLine Line(string code, string label, IbcOutcome ibc, LegalValue rate, decimal prior) =>
        new(
            code,
            PayrollLineGroup.Deduction,
            label,
            PayrollRounding.ToPeso(ibc.SocialSecurityIbc * rate.Value) - prior,
            null,
            new LineTrace(
                Formula,
                [new TraceInput("IBC del mes", ibc.SocialSecurityIbc), new TraceInput("Tasa", rate.Value), new TraceInput("Retenido en la primera quincena", prior)],
                [rate],
                null));
}
