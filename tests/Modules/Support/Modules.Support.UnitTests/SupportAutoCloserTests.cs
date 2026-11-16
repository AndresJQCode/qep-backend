using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// Fase 4b, Tarea 14 (H22, owner 2026-11-16, Q-4): la fecha del cierre automático es el N-ésimo día hábil después de la resolución o de la
/// última respuesta del cliente que sigue en el expediente, lo que sea más tarde; el ticket se cierra cuando la fecha local del tenant ya
/// pasó esa fecha, o sea al día siguiente.
/// </summary>
public sealed class SupportAutoCloserTests
{
    private static readonly DateOnly Friday = new(2026, 11, 13);

    private static readonly FakeCalendar Weekdays = new();

    [Fact]
    public void ItClosesTheDayAfterTheNthBusinessDay()
    {
        var closeOn = SupportAutoCloser.AutoCloseOn(Weekdays, Friday, null, 5);

        Assert.Equal(new DateOnly(2026, 11, 20), closeOn);
        Assert.False(SupportAutoCloser.IsDue(closeOn, new DateOnly(2026, 11, 20)));
        Assert.True(SupportAutoCloser.IsDue(closeOn, new DateOnly(2026, 11, 21)));
        Assert.Equal(new DateOnly(2026, 11, 16), SupportAutoCloser.AutoCloseOn(Weekdays, Friday, null, 1));
    }

    // H22: la cuenta vuelve a empezar desde la última respuesta del cliente; una anterior a la resolución no la mueve.
    [Fact]
    public void ALaterCustomerReplyRestartsTheCount()
    {
        Assert.Equal(new DateOnly(2026, 11, 25), SupportAutoCloser.AutoCloseOn(Weekdays, Friday, new DateOnly(2026, 11, 18), 5));
        Assert.Equal(new DateOnly(2026, 11, 20), SupportAutoCloser.AutoCloseOn(Weekdays, Friday, new DateOnly(2026, 11, 12), 5));
    }

    // El calendario de Soporte (lunes a viernes menos los festivos de Colombia): el lunes festivo 16 y el fin de semana no cuentan.
    [Fact]
    public void HolidaysAndWeekendsDoNotCount()
    {
        Assert.Equal(new DateOnly(2026, 11, 23), SupportAutoCloser.AutoCloseOn(FakeCalendar.WithHolidays, Friday, null, 5));
        Assert.Equal(new DateOnly(2026, 11, 17), SupportAutoCloser.AutoCloseOn(FakeCalendar.WithHolidays, Friday, null, 1));
        Assert.Equal(new DateOnly(2026, 12, 28), SupportAutoCloser.AutoCloseOn(FakeCalendar.WithHolidays, new DateOnly(2026, 12, 24), null, 1));
    }
}
