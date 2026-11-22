namespace Modules.Payroll.Domain.Engine;

public sealed record IncomeOutcome(IReadOnlyList<PayrollLine> Lines, decimal SalaryIncome, decimal NonSalaryIncome);

/// <summary>Paso 3 (parte capturada): cada ingreso capturado es una línea redondeada al peso, salarial o no según lo elegido.</summary>
public static class IncomeRule
{
    private const string Formula = "redondeo al peso(valor capturado)";

    public static IncomeOutcome Compute(EngineInput input)
    {
        var lines = new List<PayrollLine>(input.Incomes.Count);
        var salaryIncome = 0m;
        var nonSalaryIncome = 0m;
        for (var position = 0; position < input.Incomes.Count; position++)
        {
            var income = input.Incomes[position];
            var amount = PayrollRounding.ToPeso(income.Amount);
            if (income.SalaryConstitutive)
            {
                salaryIncome += amount;
            }
            else
            {
                nonSalaryIncome += amount;
            }

            lines.Add(new PayrollLine(
                $"income:{income.ConceptId}:{position}",
                PayrollLineGroup.Earning,
                income.ConceptName,
                amount,
                income.SalaryConstitutive,
                new LineTrace(
                    Formula,
                    [new TraceInput("Valor capturado", income.Amount), new TraceInput("Constitutivo de salario", income.SalaryConstitutive)],
                    [],
                    null)));
        }

        return new IncomeOutcome(lines, salaryIncome, nonSalaryIncome);
    }
}
