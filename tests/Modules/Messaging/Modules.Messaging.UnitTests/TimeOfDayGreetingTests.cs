using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Las franjas del saludo: «Buenos días» de 00:00 a 11:59, «Buenas tardes» de 12:00 a 17:59 y «Buenas noches» de 18:00 a 23:59.</summary>
public sealed class TimeOfDayGreetingTests
{
    [Theory]
    [InlineData(0, 0, 0, "Buenos días")]
    [InlineData(11, 59, 59, "Buenos días")]
    [InlineData(12, 0, 0, "Buenas tardes")]
    [InlineData(17, 59, 59, "Buenas tardes")]
    [InlineData(18, 0, 0, "Buenas noches")]
    [InlineData(23, 59, 59, "Buenas noches")]
    public void EachBandStartsOnTheHour(int hour, int minute, int second, string greeting) =>
        Assert.Equal(greeting, TimeOfDayGreeting.For(new TimeOnly(hour, minute, second)));
}
