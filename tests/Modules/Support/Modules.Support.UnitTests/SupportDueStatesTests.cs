using System.Globalization;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Vence 2026-12-07 (lunes), con festivo el 2026-12-08; umbral 3 salvo que se diga.
public sealed class SupportDueStatesTests
{
    private static readonly ISupportBusinessCalendar Calendar = FakeCalendar.WithHolidays;
    private static readonly DateOnly DueOn = new(2026, 12, 7);

    [Theory]
    [InlineData("2026-12-08", 3, SupportDueState.Overdue, 0)]
    [InlineData("2026-12-02", 3, SupportDueState.DueSoon, 3)]
    [InlineData("2026-12-01", 3, SupportDueState.OnTrack, 4)]
    [InlineData("2026-12-01", 5, SupportDueState.DueSoon, 4)]
    [InlineData("2026-12-07", 3, SupportDueState.DueSoon, 0)]
    [InlineData("2026-12-07", 0, SupportDueState.OnTrack, 0)]
    [InlineData("2026-12-04", 0, SupportDueState.OnTrack, 1)]
    public void ARunningTicketIsMeasuredAgainstItsDueDate(string today, int threshold, SupportDueState state, int left) =>
        Assert.Equal(
            (state, (int?)left),
            SupportDueStates.Evaluate(SupportTicketStatus.Open, DueOn, DateOnly.Parse(today, CultureInfo.InvariantCulture), Calendar, threshold));

    [Theory]
    [InlineData(SupportTicketStatus.New)]
    [InlineData(SupportTicketStatus.WaitingInternal)]
    public void NewAndWaitingInternalKeepTheClockRunning(SupportTicketStatus status) =>
        Assert.Equal(
            (SupportDueState.DueSoon, (int?)3),
            SupportDueStates.Evaluate(status, DueOn, new DateOnly(2026, 12, 2), Calendar, 3));

    [Fact]
    public void WaitingOnCustomerIsPaused() =>
        Assert.Equal(
            (SupportDueState.Paused, (int?)null),
            SupportDueStates.Evaluate(SupportTicketStatus.WaitingOnCustomer, DueOn, new DateOnly(2026, 12, 8), Calendar, 3));

    [Theory]
    [InlineData(SupportTicketStatus.Resolved)]
    [InlineData(SupportTicketStatus.Closed)]
    public void ResolvedAndClosedAreFinished(SupportTicketStatus status) =>
        Assert.Equal(
            (SupportDueState.Finished, (int?)null),
            SupportDueStates.Evaluate(status, DueOn, new DateOnly(2026, 12, 8), Calendar, 3));

    [Fact]
    public void ARunningTicketWithoutDueDateHasNoTerm() =>
        Assert.Equal(
            (SupportDueState.NoTerm, (int?)null),
            SupportDueStates.Evaluate(SupportTicketStatus.Open, null, new DateOnly(2026, 12, 2), Calendar, 3));

    // Pre-flight R11: Paused y Finished le ganan a NoTerm sólo cuando el estado detiene el reloj.
    [Theory]
    [InlineData(SupportTicketStatus.New, SupportDueState.NoTerm)]
    [InlineData(SupportTicketStatus.WaitingInternal, SupportDueState.NoTerm)]
    [InlineData(SupportTicketStatus.WaitingOnCustomer, SupportDueState.Paused)]
    [InlineData(SupportTicketStatus.Resolved, SupportDueState.Finished)]
    [InlineData(SupportTicketStatus.Closed, SupportDueState.Finished)]
    public void WithoutDueDateAStoppedClockStillSaysWhyItStopped(SupportTicketStatus status, SupportDueState expected) =>
        Assert.Equal(
            (expected, (int?)null),
            SupportDueStates.Evaluate(status, null, new DateOnly(2026, 12, 2), Calendar, 3));
}
