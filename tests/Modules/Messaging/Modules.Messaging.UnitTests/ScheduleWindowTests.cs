using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09: la hora de un mensaje programado debe caer al menos un minuto adelante y antes de que la ventana de 24 h
/// del cliente cierre, con dos minutos de margen para que no salga justo cuando Meta la cierra.</summary>
public sealed class ScheduleWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiftyNineSecondsAheadIsTooSoon()
    {
        var error = Assert.Throws<MessagingDomainException>(
            () => ScheduledMessage.EnsureSchedulable(Now.AddSeconds(59), Now, Now));

        Assert.Equal(MessagingErrorCodes.ScheduledTooSoon, error.Code);
    }

    [Fact]
    public void OneMinuteBeforeExpiryIsOutsideWindow()
    {
        var lastInbound = Now.AddHours(-1);
        var error = Assert.Throws<MessagingDomainException>(
            () => ScheduledMessage.EnsureSchedulable(lastInbound.AddHours(24).AddMinutes(-1), lastInbound, Now));

        Assert.Equal(MessagingErrorCodes.ScheduledOutsideWindow, error.Code);
    }

    [Fact]
    public void ExactlyTwoMinutesBeforeExpiryPasses()
    {
        var lastInbound = Now.AddHours(-1);

        ScheduledMessage.EnsureSchedulable(lastInbound.AddHours(24).AddMinutes(-2), lastInbound, Now);
    }

    [Fact]
    public void WithoutLastInboundItIsOutsideWindow()
    {
        var error = Assert.Throws<MessagingDomainException>(
            () => ScheduledMessage.EnsureSchedulable(Now.AddHours(1), null, Now));

        Assert.Equal(MessagingErrorCodes.ScheduledOutsideWindow, error.Code);
    }

    [Fact]
    public void TheWindowForSendingClosesTwoMinutesEarly()
    {
        var lastInbound = Now.AddHours(-24).AddMinutes(2);

        Assert.False(ScheduledMessage.IsWindowOpenForSending(lastInbound, Now));
        Assert.True(ScheduledMessage.IsWindowOpenForSending(lastInbound.AddSeconds(1), Now));
        Assert.False(ScheduledMessage.IsWindowOpenForSending(null, Now));
    }

    [Fact]
    public void GreetingTokenCountsAsThirteen()
    {
        Assert.Equal(13 + 6, ScheduledText.MeasuredLength("{saludo}, hola"));
        Assert.Equal("Buenas tardes, hola", ScheduledText.ResolveGreeting("{saludo}, hola", "Buenas tardes"));
    }
}
