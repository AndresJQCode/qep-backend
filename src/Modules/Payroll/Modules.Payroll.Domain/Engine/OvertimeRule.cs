namespace Modules.Payroll.Domain.Engine;

public sealed record OvertimeOutcome(IReadOnlyList<PayrollLine> Lines, decimal Total, decimal HourValue);

/// <summary>Paso 5: el valor hora (sin redondear, M-5) por las horas y el factor de cada captura; sólo la línea se redondea.</summary>
public static class OvertimeRule
{
    private const string Formula = "redondeo al peso(horas × valor hora × factor); valor hora = base ÷ 30 ÷ (jornada semanal ÷ 6)";

    public static OvertimeOutcome Compute(EngineInput input, decimal salaryInForce, decimal salaryIncomeMonth)
    {
        var baseValue = input.Settings.OvertimeBase == OvertimeBase.SalaryAndMonthSalaryIncome
            ? salaryInForce + salaryIncomeMonth
            : salaryInForce;
        var weeklyHours = input.Legal.WeeklyHours;

        // base ÷ 30 ÷ (hours ÷ 6) written as one division, so a 44-hour week does not carry a repeating 7.333… into the value.
        var hourValue = baseValue * 6m / (30m * weeklyHours.Value);

        var lines = new List<PayrollLine>(input.Overtime.Count);
        var total = 0m;
        foreach (var capture in input.Overtime)
        {
            var (factor, used) = OvertimeFactors.For(capture, input.Legal);
            var amount = PayrollRounding.ToPeso(capture.Hours * factor * baseValue * 6m / (30m * weeklyHours.Value));
            total += amount;
            lines.Add(new PayrollLine(
                $"overtime:{capture.ConceptId}",
                PayrollLineGroup.Earning,
                capture.ConceptName,
                amount,
                null,
                new LineTrace(
                    Formula,
                    [
                        new TraceInput("Horas", capture.Hours),
                        new TraceInput("Base del valor hora", baseValue),
                        new TraceInput("Valor hora", hourValue),
                        new TraceInput("Factor", factor),
                    ],
                    [weeklyHours, .. used],
                    null)));
        }

        return new OvertimeOutcome(lines, total, hourValue);
    }
}
