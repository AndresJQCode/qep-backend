namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Las dos bases del mes (decisión del owner 1, 2026-11-22). <paramref name="SocialSecurityIbc"/> es el IBC de seguridad social:
/// devengo salarial más el exceso no salarial sobre el 40 %, con piso por días y tope de 25 SMMLV; base de salud, pensión, FSP y
/// ARL. <paramref name="ParafiscalBase"/> es <c>SalDev_m</c> (salario, horas e ingresos salariales del mes) sin la regla del 40 %
/// ni piso ni tope; base de caja, ICBF y SENA.
/// </summary>
public sealed record IbcOutcome(decimal SocialSecurityIbc, decimal ParafiscalBase, PayrollLine Line);

/// <summary>Paso 7: el ingreso base de cotización del mes, con la regla del 40 %, el piso por días y el tope de 25 SMMLV.</summary>
public static class IbcRule
{
    private const string Formula =
        "techo al peso(mínimo(máximo(devengo salarial del mes, o el total × (1 − tope no salarial) si lo no salarial lo supera; SMMLV ÷ 30 × días del mes); tope × SMMLV))";

    public static IbcOutcome Compute(EngineInput input, WorkedDays worked, decimal salaryLine, MonthIncome month)
    {
        var legal = input.Legal;
        var salaryEarningsMonth = input.Prior.Salary + salaryLine + month.OvertimeMonth + month.SalaryIncomeMonth;
        var total = salaryEarningsMonth + month.NonSalaryIncomeMonth;
        var overTheCap = month.NonSalaryIncomeMonth > legal.NonSalaryCapRate.Value * total;
        var raw = overTheCap ? total * (1m - legal.NonSalaryCapRate.Value) : salaryEarningsMonth;

        var daysMonth = input.Prior.WorkedDays + worked.Days;
        var floor = legal.Smmlv.Value * daysMonth / 30m;
        var cap = legal.IbcCapSmmlv.Value * legal.Smmlv.Value;
        var ibc = PayrollRounding.CeilingToPeso(Math.Min(Math.Max(raw, floor), cap));

        var line = new PayrollLine(
            "ibc",
            PayrollLineGroup.Info,
            "Ingreso base de cotización del mes",
            ibc,
            null,
            new LineTrace(
                Formula,
                [
                    new TraceInput("Devengo salarial del mes", salaryEarningsMonth),
                    new TraceInput("Pagos no salariales del mes", month.NonSalaryIncomeMonth),
                    new TraceInput("Días del mes", daysMonth),
                    new TraceInput("Base antes de piso y tope", raw),
                ],
                [legal.NonSalaryCapRate, legal.IbcCapSmmlv, legal.Smmlv],
                overTheCap ? "Los pagos no salariales superan el 40 % del total: el exceso se suma al IBC" : null));
        return new IbcOutcome(ibc, salaryEarningsMonth, line);
    }
}
