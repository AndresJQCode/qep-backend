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
        decimal salaryIncome)
    {
        var legal = input.Legal;
        var fullBase = salary + transport + overtime + salaryIncome;
        var vacationBase = salary + salaryIncome;

        return
        [
            Line("severance", "Cesantías", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.SeveranceProvisionRate),
            Line("severance_interest", "Intereses a las cesantías", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.SeveranceInterestProvisionRate),
            Line("premium", "Prima de servicios", "Salario, horas, ingresos salariales y auxilio", fullBase, legal.PremiumProvisionRate),
            Line("vacation", "Vacaciones", "Salario e ingresos salariales", vacationBase, legal.VacationProvisionRate),
        ];
    }

    private static PayrollLine Line(string code, string label, string baseName, decimal baseAmount, LegalValue rate) =>
        new(
            code,
            PayrollLineGroup.Provision,
            label,
            PayrollRounding.ToPeso(baseAmount * rate.Value),
            null,
            new LineTrace(Formula, [new TraceInput(baseName, baseAmount), new TraceInput("Tasa", rate.Value)], [rate], null));
}
