namespace Modules.Payroll.Domain.Engine;

public sealed record RecurringOutcome(
    IReadOnlyList<PayrollLine> IncomeLines,
    IReadOnlyList<PayrollLine> DeductionLines,
    decimal SalaryIncome,
    decimal NonSalaryIncome,
    decimal Deductions);

/// <summary>Pasos 3 y 10 (parte recurrente): proporcional a los días, limitado por el tope que le queda, redondeado al peso.</summary>
public static class RecurringConceptRule
{
    private const string Formula =
        "redondeo al peso(mínimo entre el valor del período y el tope restante); el valor proporcional es valor × días trabajados ÷ 15";

    public static RecurringOutcome Compute(EngineInput input, WorkedDays worked) =>
        Compute(input, worked, VacationDaysOutcome.None);

    public static RecurringOutcome Compute(EngineInput input, WorkedDays worked, VacationDaysOutcome vacations)
    {
        // Owner ruling (2026-11-23): a proportional concept follows the days actually worked, so vacation days do not pay it.
        var workedDays = worked.Days - vacations.Days;

        var incomeLines = new List<PayrollLine>();
        var deductionLines = new List<PayrollLine>();
        var salaryIncome = 0m;
        var nonSalaryIncome = 0m;
        var deductions = 0m;

        foreach (var recurring in input.Recurring)
        {
            if (recurring.Kind == ConceptKind.Overtime)
            {
                throw new InvalidOperationException("A recurring concept is an income or a deduction, never overtime.");
            }

            var value = recurring.Mode == RecurringMode.Proportional ? recurring.Amount * workedDays / 15m : recurring.Amount;
            if (recurring.RemainingCap is { } remaining)
            {
                value = Math.Min(value, remaining);
            }

            var amount = PayrollRounding.ToPeso(value);
            if (amount <= 0m)
            {
                continue;
            }

            var isIncome = recurring.Kind == ConceptKind.Income;
            var inputs = new List<TraceInput>
            {
                new("Valor del concepto", recurring.Amount),
                new("Modo", recurring.Mode.ToString()),
                new("Días trabajados", workedDays),
            };
            if (recurring.RemainingCap is { } cap)
            {
                inputs.Add(new TraceInput("Tope restante", cap));
            }

            var line = new PayrollLine(
                $"recurring:{recurring.RecurringId}",
                isIncome ? PayrollLineGroup.Earning : PayrollLineGroup.Deduction,
                recurring.ConceptName,
                amount,
                isIncome ? recurring.SalaryConstitutive : null,
                new LineTrace(Formula, inputs, [], null));

            if (!isIncome)
            {
                deductionLines.Add(line);
                deductions += amount;
            }
            else
            {
                incomeLines.Add(line);
                if (recurring.SalaryConstitutive)
                {
                    salaryIncome += amount;
                }
                else
                {
                    nonSalaryIncome += amount;
                }
            }
        }

        return new RecurringOutcome(incomeLines, deductionLines, salaryIncome, nonSalaryIncome, deductions);
    }
}
