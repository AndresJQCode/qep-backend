using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21, «Citas»: the client, through a channel, may cancel until `client_cancel_cutoff_hours` before the start; the back office has no such limit.
public sealed class AppointmentCancellationRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 7, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AClientMayCancelExactlyAtTheCutoff() =>
        Assert.True(AppointmentCancellationRules.ClientMayCancel(Start, Start.AddHours(-24), 24));

    [Fact]
    public void AClientMayNotCancelOneMinuteInsideTheCutoff() =>
        Assert.False(AppointmentCancellationRules.ClientMayCancel(Start, Start.AddHours(-24).AddMinutes(1), 24));

    [Fact]
    public void WithoutACutoffAClientMayCancelUntilTheStartButNotAfter()
    {
        Assert.True(AppointmentCancellationRules.ClientMayCancel(Start, Start.AddMinutes(-1), 0));
        Assert.True(AppointmentCancellationRules.ClientMayCancel(Start, Start, 0));
        Assert.False(AppointmentCancellationRules.ClientMayCancel(Start, Start.AddMinutes(1), 0));
    }

    [Fact]
    public void TheMessageSaysHowManyHoursAreNeeded() =>
        Assert.Contains("24 horas", AppointmentCancellationRules.TooLateMessage(24), StringComparison.Ordinal);

    // D-15: a client moves an appointment under the same cutoff that rules its cancellation.
    [Fact]
    public void ChangeByChatUntilIsTheStartMinusTheCutoff()
    {
        Assert.Equal(Start.AddHours(-24), AppointmentCancellationRules.ChangeByChatUntil(Start, 24));
        Assert.Equal(Start, AppointmentCancellationRules.ChangeByChatUntil(Start, 0));
    }

    [Fact]
    public void TheMoveMessagesSayWhatTheClientCanDo()
    {
        Assert.Contains("24 horas", AppointmentCancellationRules.TooLateToMoveMessage(24), StringComparison.Ordinal);
        Assert.Equal("Esta cita sólo la puede mover el negocio.", AppointmentCancellationRules.NotMovableByChatMessage);
    }
}
