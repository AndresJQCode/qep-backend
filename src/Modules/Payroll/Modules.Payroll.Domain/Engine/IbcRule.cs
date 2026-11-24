namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Las tres bases del mes (decisión del owner 1, 2026-11-22, y spec de vacaciones «IBC: tres bases por mes»).
/// <paramref name="SocialSecurityIbc"/> es el IBC de seguridad social: devengo salarial más el exceso no salarial sobre el 40 %,
/// más la parte de los días de vacaciones del mes, con piso por días y tope de 25 SMMLV; base de salud, pensión y FSP.
/// <paramref name="ArlBase"/> es lo mismo sin la parte de vacaciones y con piso sólo por días trabajados: los días de vacaciones
/// no cotizan ARL. <paramref name="ParafiscalBase"/> es <c>SalDev_m</c> (salario, horas e ingresos salariales del mes) más el
/// valor de los días de vacaciones, sin la regla del 40 % ni piso ni tope; base de caja, ICBF y SENA.
/// </summary>
public sealed record IbcOutcome(decimal SocialSecurityIbc, decimal ArlBase, decimal ParafiscalBase, PayrollLine Line);

/// <summary>Paso 7: el ingreso base de cotización del mes, con la regla del 40 %, el piso por días y el tope de 25 SMMLV.</summary>
public static class IbcRule
{
    private const string Formula =
        "techo al peso(mínimo(máximo(devengo salarial del mes, o el total × (1 − tope no salarial) si lo no salarial lo supera; SMMLV ÷ 30 × días del mes); tope × SMMLV))";

    private const string FallbackNote = "Sin IBC del mes anterior en QEP: se usa el salario";

    public static IbcOutcome Compute(EngineInput input, WorkedDays worked, decimal salaryLine, MonthIncome month) =>
        Compute(input, worked, VacationDaysOutcome.None, salaryLine, month);

    public static IbcOutcome Compute(
        EngineInput input,
        WorkedDays worked,
        VacationDaysOutcome vacations,
        decimal salaryLine,
        MonthIncome month)
    {
        var legal = input.Legal;
        var salaryEarningsMonth = input.Prior.Salary + salaryLine + month.OvertimeMonth + month.SalaryIncomeMonth;
        var total = salaryEarningsMonth + month.NonSalaryIncomeMonth;
        var overTheCap = month.NonSalaryIncomeMonth > legal.NonSalaryCapRate.Value * total;
        var raw = overTheCap ? total * (1m - legal.NonSalaryCapRate.Value) : salaryEarningsMonth;

        // The floor counts days worked plus the vacation days of the month, but the ARL floor only the days worked: vacation days
        // do not contribute to ARL.
        var workedMonth = input.Prior.WorkedDays + worked.Days - vacations.Days;
        var daysMonth = workedMonth + vacations.MonthDays;
        var cap = legal.IbcCapSmmlv.Value * legal.Smmlv.Value;
        var floor = legal.Smmlv.Value * daysMonth / 30m;
        var ibc = PayrollRounding.CeilingToPeso(Math.Min(Math.Max(raw + vacations.IbcPart, floor), cap));
        var arlFloor = legal.Smmlv.Value * workedMonth / 30m;
        var arlBase = PayrollRounding.CeilingToPeso(Math.Min(Math.Max(raw, arlFloor), cap));

        var inputs = new List<TraceInput>
        {
            new("Devengo salarial del mes", salaryEarningsMonth),
            new("Pagos no salariales del mes", month.NonSalaryIncomeMonth),
            new("Días del mes", daysMonth),
            new("Base antes de piso y tope", raw),
        };
        var notes = new List<string>();
        if (overTheCap)
        {
            notes.Add("Los pagos no salariales superan el 40 % del total: el exceso se suma al IBC");
        }

        if (vacations.MonthDays > 0)
        {
            inputs.AddRange(input.Vacations.Select(vacation => new TraceInput("IBC anterior a las vacaciones", vacation.PreviousMonthIbc)));
            inputs.Add(new TraceInput("Días de vacaciones del mes", vacations.MonthDays));
            inputs.Add(new TraceInput("Parte de vacaciones", vacations.IbcPart));
            if (input.Vacations.Any(vacation => vacation.PreviousIbcSource == VacationIbcSource.Salary))
            {
                notes.Add(FallbackNote);
            }
        }

        var line = new PayrollLine(
            "ibc",
            PayrollLineGroup.Info,
            "Ingreso base de cotización del mes",
            ibc,
            null,
            new LineTrace(
                Formula,
                inputs,
                [legal.NonSalaryCapRate, legal.IbcCapSmmlv, legal.Smmlv],
                notes.Count == 0 ? null : string.Join("; ", notes)));
        return new IbcOutcome(ibc, arlBase, salaryEarningsMonth + vacations.ParafiscalValue, line);
    }
}
