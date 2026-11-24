namespace Modules.Payroll.Domain.Engine;

/// <summary>Paso 12 (R-21): las cuatro provisiones del período, siempre, sin acumular el mes.</summary>
public static class ProvisionsRule
{
    private const string Formula = "redondeo al peso(base × tasa)";

    public static IReadOnlyList<PayrollLine> Compute(
        EngineInput input,
        decimal salary,
        decimal transport,
        decimal overtime,
        decimal salaryIncome) =>
        Compute(input, salary, transport, overtime, salaryIncome, 0m);

    /// <param name="serviceValue">
    /// What the vacation days of this quincena are worth (salary of the start ÷ 30 × days). They count as service time (CST art. 186),
    /// so they enter every base; the <c>vacation_pay</c> line itself never does, or December would provision January.
    /// </param>
    public static IReadOnlyList<PayrollLine> Compute(
        EngineInput input,
        decimal salary,
        decimal transport,
        decimal overtime,
        decimal salaryIncome,
        decimal serviceValue)
    {
        var legal = input.Legal;
        var fullBase = salary + transport + overtime + salaryIncome + serviceValue;
        var vacationBase = salary + salaryIncome + serviceValue;

        return
        [
            Line("severance", "Cesantías", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.SeveranceProvisionRate, serviceValue),
            Line("severance_interest", "Intereses a las cesantías", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.SeveranceInterestProvisionRate, serviceValue),
            Line("premium", "Prima de servicios", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.PremiumProvisionRate, serviceValue),
            Line("vacation", "Vacaciones", "Salario e ingresos salariales", vacationBase, legal.VacationProvisionRate, serviceValue),
        ];
    }

    private static PayrollLine Line(string code, string label, string baseName, decimal baseAmount, LegalValue rate, decimal serviceValue)
    {
        var inputs = new List<TraceInput> { new(baseName, baseAmount), new("Tasa", rate.Value) };
        if (serviceValue > 0m)
        {
            inputs.Add(new TraceInput("Días de vacaciones como tiempo de servicio", serviceValue));
        }

        return new PayrollLine(
            code,
            PayrollLineGroup.Provision,
            label,
            PayrollRounding.ToPeso(baseAmount * rate.Value),
            null,
            new LineTrace(Formula, inputs, [rate], null));
    }
}
