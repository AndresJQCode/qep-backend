namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Paso 11 (R-3, R-4, R-5, R-20): las seis líneas del empleador, siempre. Salud y pensión van sobre el IBC; ARL, caja, ICBF y SENA
/// sobre el devengo salarial del mes (sin la regla del 40 %). Exonerada, salud, ICBF y SENA salen en 0 con su nota.
/// </summary>
public static class EmployerContributionsRule
{
    private const string Formula = "techo100(base del mes × tasa) − lo aportado en la primera quincena";
    private const string ExemptNote = "Exonerado (ET art. 114-1)";

    public static IReadOnlyList<PayrollLine> Compute(EngineInput input, IbcOutcome ibc, MonthIncome month)
    {
        var legal = input.Legal;
        var prior = input.Prior;
        var exempt = input.Settings.Exonerated
            && month.MonthSalaryIncome < legal.ExonerationThresholdSmmlv.Value * legal.Smmlv.Value;
        var extra = new[] { legal.ExonerationThresholdSmmlv, legal.Smmlv };

        var highRisk = input.Workplace.HighRiskPension;
        var pensionRate = highRisk ? legal.HighRiskPensionTotalRate.Value - legal.PensionEmployeeRate.Value : legal.PensionEmployerRate.Value;
        LegalValue[] pensionLegal = highRisk
            ? [legal.HighRiskPensionTotalRate, legal.PensionEmployeeRate]
            : [legal.PensionEmployerRate];
        var arl = legal.ArlRate(input.Workplace.RiskClass);

        return
        [
            Line("health_employer", "Salud (empleador)", "IBC del mes", ibc.IbcMonth, legal.HealthEmployerRate.Value, [legal.HealthEmployerRate, .. extra], prior.HealthEmployer, exempt),
            Line("pension_employer", "Pensión (empleador)", "IBC del mes", ibc.IbcMonth, pensionRate, pensionLegal, prior.PensionEmployer, false),
            Line("arl", "ARL", "Devengo salarial del mes", ibc.SalaryEarningsMonth, arl.Value, [arl], prior.Arl, false),
            Line("compensation_fund", "Caja de compensación", "Devengo salarial del mes", ibc.SalaryEarningsMonth, legal.CompensationFundRate.Value, [legal.CompensationFundRate], prior.CompensationFund, false),
            Line("icbf", "ICBF", "Devengo salarial del mes", ibc.SalaryEarningsMonth, legal.IcbfRate.Value, [legal.IcbfRate, .. extra], prior.Icbf, exempt),
            Line("sena", "SENA", "Devengo salarial del mes", ibc.SalaryEarningsMonth, legal.SenaRate.Value, [legal.SenaRate, .. extra], prior.Sena, exempt),
        ];
    }

    private static PayrollLine Line(
        string code,
        string label,
        string baseName,
        decimal baseAmount,
        decimal rate,
        IReadOnlyList<LegalValue> legalValues,
        decimal prior,
        bool exempt)
    {
        var amount = exempt ? 0m : PayrollRounding.CeilingToHundred(baseAmount * rate) - prior;
        return new PayrollLine(
            code,
            PayrollLineGroup.EmployerContribution,
            label,
            amount,
            null,
            new LineTrace(
                Formula,
                [new TraceInput(baseName, baseAmount), new TraceInput("Tasa", rate), new TraceInput("Aportado en la primera quincena", prior)],
                legalValues,
                exempt ? ExemptNote : null));
    }
}
