using System.Globalization;

namespace Modules.Payroll.Domain.Engine;

public sealed record VacationAdvanceOutcome(IReadOnlyList<PayrollLine> Lines, IReadOnlyList<VacationPhoto> Photos);

/// <summary>What one month of a vacation contributed to an advance line, as the trace shows it.</summary>
public sealed record VacationAdvanceMonthTrace(decimal Part, decimal Calculated, decimal Withheld);

/// <summary>
/// The quincena that pays a vacation withholds in advance the worker's social security for the vacation days that fall outside
/// it (spec «Álgebra del anticipo»), grouped by month. What it withholds is bounded by the net it has, and the photo keeps the
/// withheld amount, not the computed one.
/// </summary>
public static class VacationAdvanceRule
{
    public const string HealthCode = "health_employee_vacation_advance";
    public const string PensionCode = "pension_employee_vacation_advance";
    public const string SolidarityFundCode = "solidarity_fund_vacation_advance";

    private const string LeftOutNote = "Se retiene hasta dejar el neto en 0";

    /// <param name="available">The net of the quincena before the advance lines; what exceeds it is not withheld.</param>
    public static VacationAdvanceOutcome Compute(EngineInput input, decimal available)
    {
        var legal = input.Legal;
        var remaining = Math.Max(0m, available);
        var health = new List<Entry>();
        var pension = new List<Entry>();
        var fund = new List<Entry>();
        var photos = new List<VacationPhoto>();

        foreach (var vacation in input.Vacations.Where(item => item.PaidHere && item.Origin == VacationOrigin.Qep))
        {
            var quincenas = VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate);
            // A typed IBC above the month cap would over-withhold what the month IBC (capped) never refunds; the photo keeps what was typed.
            var ibc = Math.Min(vacation.PreviousMonthIbc, legal.IbcCapSmmlv.Value * legal.Smmlv.Value);
            var fundRate = legal.SolidarityRate(ibc / legal.Smmlv.Value);
            var months = new List<VacationMonthAdvance>();

            // The other half of the paying month and every later month; this quincena's own days go in the regular lines.
            foreach (var month in quincenas.Where(item => item.Start != input.Period.Start).GroupBy(item => new DateOnly(item.Start.Year, item.Start.Month, 1)))
            {
                var part = PayrollRounding.CeilingToPeso(ibc * month.Sum(item => item.Days) / 30m);
                var healthDue = PayrollRounding.ToPeso(legal.HealthEmployeeRate.Value * part);
                var pensionDue = PayrollRounding.ToPeso(legal.PensionEmployeeRate.Value * part);
                var fundDue = fundRate is { } rate ? PayrollRounding.CeilingToHundred(part * rate) : 0m;

                var healthPaid = Take(ref remaining, healthDue);
                var pensionPaid = Take(ref remaining, pensionDue);
                var fundPaid = Take(ref remaining, fundDue);

                var name = month.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                health.Add(new Entry(name, part, healthDue, healthPaid));
                pension.Add(new Entry(name, part, pensionDue, pensionPaid));
                fund.Add(new Entry(name, part, fundDue, fundPaid));
                months.Add(new VacationMonthAdvance(month.Key, part, healthPaid, pensionPaid, fundPaid));
            }

            photos.Add(new VacationPhoto(
                vacation.VacationId,
                vacation.SalaryAtStart,
                vacation.SalaryRecordDate,
                vacation.PreviousMonthIbc,
                vacation.PreviousIbcSource,
                quincenas,
                months));
        }

        var lines = new List<PayrollLine>();
        if (health.Count > 0)
        {
            lines.Add(Line(HealthCode, "Anticipo de salud por vacaciones", health, [legal.HealthEmployeeRate]));
            lines.Add(Line(PensionCode, "Anticipo de pensión por vacaciones", pension, [legal.PensionEmployeeRate]));
            if (fund.Sum(entry => entry.Withheld) > 0m)
            {
                lines.Add(Line(SolidarityFundCode, "Anticipo del FSP por vacaciones", fund, [legal.Smmlv]));
            }
        }

        return new VacationAdvanceOutcome(lines, photos);
    }

    private static decimal Take(ref decimal remaining, decimal due)
    {
        var taken = Math.Min(remaining, due);
        remaining -= taken;
        return taken;
    }

    private static PayrollLine Line(string code, string label, List<Entry> entries, IReadOnlyList<LegalValue> legalValues) =>
        new(
            code,
            PayrollLineGroup.Deduction,
            label,
            entries.Sum(entry => entry.Withheld),
            null,
            new LineTrace(
                "Σ por mes: tasa × techo al peso(IBC anterior ÷ 30 × días de vacaciones del mes), hasta dejar el neto en 0",
                entries.Select(entry => new TraceInput(entry.Month, new VacationAdvanceMonthTrace(entry.Part, entry.Calculated, entry.Withheld))).ToList(),
                legalValues,
                entries.Sum(entry => entry.Withheld) < entries.Sum(entry => entry.Calculated) ? LeftOutNote : null));

    private sealed record Entry(string Month, decimal Part, decimal Calculated, decimal Withheld);
}
