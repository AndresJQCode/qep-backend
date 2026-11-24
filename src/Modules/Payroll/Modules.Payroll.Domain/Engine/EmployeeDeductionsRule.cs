namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Paso 8: salud y pensión del empleado, sobre el IBC del mes y descontando lo retenido en la primera quincena (R-5). Al trabajador se le
/// descuenta el 4 % al peso, como Siigo: la aproximación a la centena (Decreto 1990/2016) es de la cotización en la PILA, que asume el
/// empleador, no del descuento de nómina (decisión del owner, 2026-11-23). Con vacaciones, descuenta además el anticipo del mes
/// (término (b)) que retuvo la quincena que las paga, sin bajar nunca de 0 por él.
/// </summary>
public static class EmployeeDeductionsRule
{
    private const string Formula =
        "al peso(IBC del mes × tasa) − lo retenido en la primera quincena − el anticipo de vacaciones del mes (nunca baja de 0 por el anticipo)";

    public static IReadOnlyList<PayrollLine> Compute(EngineInput input, IbcOutcome ibc) =>
        Compute(input, ibc, VacationDaysOutcome.None);

    public static IReadOnlyList<PayrollLine> Compute(EngineInput input, IbcOutcome ibc, VacationDaysOutcome vacations) =>
    [
        Line("health_employee", "Salud", ibc, input.Legal.HealthEmployeeRate, input.Prior.HealthEmployee, vacations.MonthAdvance.Health),
        Line("pension_employee", "Pensión", ibc, input.Legal.PensionEmployeeRate, input.Prior.PensionEmployee, vacations.MonthAdvance.Pension),
    ];

    private static PayrollLine Line(string code, string label, IbcOutcome ibc, LegalValue rate, decimal prior, decimal advance)
    {
        var due = PayrollRounding.ToPeso(ibc.SocialSecurityIbc * rate.Value) - prior;
        var inputs = new List<TraceInput>
        {
            new("IBC del mes", ibc.SocialSecurityIbc),
            new("Tasa", rate.Value),
            new("Retenido en la primera quincena", prior),
        };
        if (advance > 0m)
        {
            inputs.Add(new TraceInput("Anticipo de vacaciones del mes", advance));
        }

        // The advance was withheld in a quincena that could afford it, but never more than this quincena owes: a refund here would
        // return money the next quincena cannot withhold again.
        return new PayrollLine(
            code,
            PayrollLineGroup.Deduction,
            label,
            due - Math.Min(advance, Math.Max(0m, due)),
            null,
            new LineTrace(Formula, inputs, [rate], null));
    }
}
