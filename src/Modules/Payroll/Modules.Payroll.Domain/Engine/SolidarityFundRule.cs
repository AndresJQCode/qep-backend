namespace Modules.Payroll.Domain.Engine;

/// <summary>Paso 9 (R-9): fondo de solidaridad pensional. La primera quincena decide por el salario; la segunda liquida el mes y puede devolver.</summary>
public static class SolidarityFundRule
{
    private const string Formula =
        "techo100(IBC del mes × tasa del tramo) − lo retenido en la primera quincena − el anticipo de vacaciones del mes (nunca baja de 0 por el anticipo)";
    private const string RefundNote = "Devolución de lo retenido en la primera quincena";

    public static PayrollLine? Compute(EngineInput input, decimal salaryInForce, IbcOutcome ibc) =>
        Compute(input, salaryInForce, ibc, VacationDaysOutcome.None);

    public static PayrollLine? Compute(EngineInput input, decimal salaryInForce, IbcOutcome ibc, VacationDaysOutcome vacations)
    {
        var legal = input.Legal;

        // The first half has no month yet, so the bracket comes from the salary, capped like the IBC (B-1).
        var multiple = input.Period.Half == PayrollHalf.First
            ? Math.Min(salaryInForce, legal.IbcCapSmmlv.Value * legal.Smmlv.Value) / legal.Smmlv.Value
            : ibc.SocialSecurityIbc / legal.Smmlv.Value;
        var rate = legal.SolidarityRate(multiple);
        var prior = input.Prior.SolidarityFund;
        var advance = vacations.MonthAdvance.SolidarityFund;

        if (rate is null && prior == 0m && advance == 0m)
        {
            return null;
        }

        var due = (rate is { } value ? PayrollRounding.CeilingToHundred(ibc.SocialSecurityIbc * value) : 0m) - prior;
        var amount = due - Math.Min(advance, Math.Max(0m, due));
        var legalValues = new List<LegalValue> { legal.Smmlv, legal.IbcCapSmmlv };
        var inputs = new List<TraceInput>
        {
            new("IBC del mes", ibc.SocialSecurityIbc),
            new("Salarios mínimos del tramo", multiple),
            new("Retenido en la primera quincena", prior),
        };
        if (advance > 0m)
        {
            inputs.Add(new TraceInput("Anticipo de vacaciones del mes", advance));
        }

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
