namespace Modules.Payroll.Domain.Engine;

/// <summary>Paso 9 (R-9): fondo de solidaridad pensional. La primera quincena decide por el salario; la segunda liquida el mes y puede devolver.</summary>
public static class SolidarityFundRule
{
    private const string Formula = "techo100(IBC del mes × tasa del tramo) − lo retenido en la primera quincena";
    private const string RefundNote = "Devolución de lo retenido en la primera quincena";

    public static PayrollLine? Compute(EngineInput input, decimal salaryInForce, IbcOutcome ibc)
    {
        var legal = input.Legal;

        // The first half has no month yet, so the bracket comes from the salary, capped like the IBC (B-1).
        var multiple = input.Period.Half == PayrollHalf.First
            ? Math.Min(salaryInForce, legal.IbcCapSmmlv.Value * legal.Smmlv.Value) / legal.Smmlv.Value
            : ibc.IbcMonth / legal.Smmlv.Value;
        var rate = legal.SolidarityRate(multiple);
        var prior = input.Prior.SolidarityFund;

        if (rate is null && prior == 0m)
        {
            return null;
        }

        var amount = (rate is { } value ? PayrollRounding.CeilingToHundred(ibc.IbcMonth * value) : 0m) - prior;
        var legalValues = new List<LegalValue> { legal.Smmlv, legal.IbcCapSmmlv };
        var inputs = new List<TraceInput>
        {
            new("IBC del mes", ibc.IbcMonth),
            new("Salarios mínimos del tramo", multiple),
            new("Retenido en la primera quincena", prior),
        };
        if (rate is { } bracketRate)
        {
            inputs.Add(new TraceInput("Tasa", bracketRate));
            var effective = legal.SolidarityBrackets.Count == 0 ? legal.ResolvedAt : legal.SolidarityBrackets[0].EffectiveFrom;
            legalValues.Add(new LegalValue("SolidarityFund", bracketRate, effective));
        }

        return new PayrollLine(
            "solidarity_fund",
            PayrollLineGroup.Deduction,
            "Fondo de solidaridad pensional",
            amount,
            null,
            new LineTrace(Formula, inputs, legalValues, amount < 0m ? RefundNote : null));
    }
}
