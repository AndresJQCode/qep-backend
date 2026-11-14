using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Lunes a viernes, menos una lista de festivos. El de las pruebas lleva tres: 2026-11-16, 2026-12-08 y 2026-12-25.</summary>
internal sealed class FakeCalendar(params DateOnly[] holidays) : ISupportBusinessCalendar
{
    public static readonly FakeCalendar WithHolidays = new(
        new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 25));

    private readonly HashSet<DateOnly> holidays = [.. holidays];

    public bool IsBusinessDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(day);
}

/// <summary>Un calendario sin ningún día hábil: el caso que haría girar para siempre a <see cref="SupportBusinessDays"/>.</summary>
internal sealed class NeverBusinessCalendar : ISupportBusinessCalendar
{
    public bool IsBusinessDay(DateOnly day) => false;
}
