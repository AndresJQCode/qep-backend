namespace Modules.Payroll.Domain;

/// <summary>Las quincenas de nómina: la primera va del 1 al 15 y la segunda del 16 al último día del mes (R-7).</summary>
public static class QuincenaCalendar
{
    public static (DateOnly Start, DateOnly End, PayrollHalf Half) Containing(DateOnly day)
    {
        if (day.Day <= 15)
        {
            return (new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, 15), PayrollHalf.First);
        }

        var lastDay = DateTime.DaysInMonth(day.Year, day.Month);
        return (new DateOnly(day.Year, day.Month, 16), new DateOnly(day.Year, day.Month, lastDay), PayrollHalf.Second);
    }

    /// <summary>La quincena que sigue a la que empieza en <paramref name="start"/>.</summary>
    public static (DateOnly Start, DateOnly End, PayrollHalf Half) After(DateOnly start)
    {
        var current = Containing(start);
        return Containing(current.End.AddDays(1));
    }
}
