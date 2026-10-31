using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

public sealed class ReminderCalendarTests
{
    private static readonly DateOnly Due = new(2026, 3, 10);

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-03-04", null)]          // faltan 6 días
    [InlineData("2026-03-05", "before_5")]
    [InlineData("2026-03-09", "before_5")]    // nada nuevo tocó; sigue siendo el último
    [InlineData("2026-03-10", "due")]
    [InlineData("2026-03-12", "due")]
    [InlineData("2026-03-13", "after_3")]
    [InlineData("2026-03-19", "after_3")]
    [InlineData("2026-03-20", "after_10")]
    [InlineData("2026-03-27", "after_17")]
    public void StepDueOnIsTheLatestStepNotAfterToday(string today, string? expected)
    {
        Assert.Equal(expected, ReminderCalendar.StepDueOn(Due, Date(today)));
    }

    [Fact]
    public void DateOfInvertsStepDueOn()
    {
        Assert.Equal(Date("2026-03-05"), ReminderCalendar.DateOf("before_5", Due));
        Assert.Equal(Due, ReminderCalendar.DateOf("due", Due));
        Assert.Equal(Date("2026-03-13"), ReminderCalendar.DateOf("after_3", Due));
        Assert.Equal(Date("2026-03-20"), ReminderCalendar.DateOf("after_10", Due));

        // Cada paso, pedido el día de su propia fecha, se devuelve a sí mismo: los dos lados hablan del mismo calendario.
        for (var today = Due.AddDays(-5); today <= Due.AddDays(60); today = today.AddDays(1))
        {
            var step = ReminderCalendar.StepDueOn(Due, today)!;
            Assert.Equal(step, ReminderCalendar.StepDueOn(Due, ReminderCalendar.DateOf(step, Due)));
            Assert.True(ReminderCalendar.DateOf(step, Due) <= today);
        }
    }

    [Theory]
    [InlineData("after_4")]
    [InlineData("after_2")]
    [InlineData("before_3")]
    [InlineData("tomorrow")]
    public void DateOfRejectsAStepOutsideTheCalendar(string step)
    {
        Assert.Throws<ArgumentException>(() => ReminderCalendar.DateOf(step, Due));
    }
}
