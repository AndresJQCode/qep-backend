namespace Modules.Payroll.Domain.Engine;

/// <summary>Paso 10 (parte capturada): cada deducción capturada es una línea redondeada al peso. Las recurrentes las arma <see cref="RecurringConceptRule"/>.</summary>
public static class DeductionRule
{
    private const string Formula = "redondeo al peso(valor capturado)";

    public static IReadOnlyList<PayrollLine> Compute(EngineInput input)
    {
        var lines = new List<PayrollLine>(input.Deductions.Count);
        for (var position = 0; position < input.Deductions.Count; position++)
        {
            var deduction = input.Deductions[position];
            lines.Add(new PayrollLine(
                $"deduction:{deduction.ConceptId}:{position}",
                PayrollLineGroup.Deduction,
                deduction.ConceptName,
                PayrollRounding.ToPeso(deduction.Amount),
                null,
                new LineTrace(Formula, [new TraceInput("Valor capturado", deduction.Amount)], [], null)));
        }

        return lines;
    }
}
