namespace Modules.Payroll.Domain.Engine;

public sealed record DateRange(DateOnly From, DateOnly To);

/// <summary>
/// What the vacations take from one quincena. <paramref name="Days"/> are this quincena's payroll days; <paramref name="MonthDays"/>
/// those of the month up to this quincena. <paramref name="Ranges"/> are the real dates inside the quincena, to cut salary segments.
/// </summary>
public sealed record VacationDaysOutcome(
    int Days,
    int MonthDays,
    decimal IbcPart,
    decimal ParafiscalValue,
    decimal ServiceValue,
    VacationAdvance MonthAdvance,
    IReadOnlyList<DateRange> Ranges)
{
    public static VacationDaysOutcome None { get; } = new(0, 0, 0m, 0m, 0m, VacationAdvance.Zero, []);
}

/// <summary>
/// Counts the vacation days of a quincena and of its month (spec «Días» and «IBC: tres bases por mes»). Pure: it only reads dates
/// and the numbers the input already carries.
/// </summary>
public static class VacationDaysRule
{
    private const decimal DaysPerMonth = 30m;

    public static VacationDaysOutcome Compute(EngineInput input, WorkedDays worked)
    {
        if (input.Vacations.Count == 0)
        {
            return VacationDaysOutcome.None;
        }

        var start = input.Period.Start;
        var days = 0;
        var monthDays = 0;
        var ibcPart = 0m;
        var parafiscal = 0m;
        var service = 0m;
        var health = 0m;
        var pension = 0m;
        var fund = 0m;
        var ranges = new List<DateRange>();

        foreach (var vacation in input.Vacations)
        {
            var quincenas = VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate);
            var month = quincenas.Where(item => item.Start.Year == start.Year && item.Start.Month == start.Month).ToList();
            var here = month.Where(item => item.Start == start).Sum(item => item.Days);
            var untilHere = month.Where(item => item.Start <= start).Sum(item => item.Days);
            var wholeMonth = month.Sum(item => item.Days);

            days += here;
            monthDays += untilHere;
            // Multiply first and divide once, like SalaryRule: one division keeps exact halves exact.
            ibcPart += vacation.PreviousMonthIbc * untilHere / DaysPerMonth;
            parafiscal += Value(vacation, untilHere, wholeMonth);
            service += Value(vacation, here, wholeMonth);
            health += vacation.MonthAdvance.Health;
            pension += vacation.MonthAdvance.Pension;
            fund += vacation.MonthAdvance.SolidarityFund;

            // Only the days the contract covers can cut a salary segment.
            var from = Max(Max(vacation.StartDate, start), worked.CoveredStart);
            var to = Min(Min(vacation.EndDate, input.Period.End), worked.CoveredEnd);
            if (here > 0 && from <= to)
            {
                ranges.Add(new DateRange(from, to));
            }
        }

        return new VacationDaysOutcome(
            days, monthDays, ibcPart, parafiscal, service, new VacationAdvance(health, pension, fund), ranges.OrderBy(range => range.From).ToList());
    }

    // What Siigo paid for the month spreads over the month's payroll days; without it (and for Qep) a day is the salary of the start ÷ 30.
    private static decimal Value(VacationInput vacation, int days, int wholeMonth)
    {
        if (vacation.Origin == VacationOrigin.External && vacation.MonthVacationPay is { } paid)
        {
            return wholeMonth > 0 ? paid * days / wholeMonth : 0m;
        }

        return vacation.SalaryAtStart * days / DaysPerMonth;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
