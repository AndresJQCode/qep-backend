using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>Task-notices spec 2026-11-22 §5.4 and D-1: the escalation row's transitions.</summary>
public sealed class TaskNoticeEscalationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

    private static TaskNoticeEscalation NewRow() =>
        TaskNoticeEscalation.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Now.AddMinutes(30), Now);

    [Fact]
    public void DeferMovesTheDueTimeAndGivesTheClaimBack()
    {
        var row = NewRow();
        row.Claim(Now.AddMinutes(2));
        Assert.Equal(1, row.Attempts);

        var next = Now.AddMinutes(1);
        row.Defer(next);

        Assert.Equal(next, row.DueAt);
        Assert.Null(row.ClaimedUntil);
        Assert.Equal(0, row.Attempts);
        Assert.Equal("[]", row.ChannelAttemptsJson);
        Assert.Equal(TaskNoticeEscalationStatus.Pending, row.Status);
    }

    [Fact]
    public void DeferNeverGoesBelowZeroAttempts()
    {
        var row = NewRow();

        row.Defer(Now.AddMinutes(1));

        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public void DeferOutsidePendingThrows()
    {
        var row = NewRow();
        row.Skip(TaskNoticeSkipReasons.Read, Now);

        Assert.Throws<InvalidOperationException>(() => row.Defer(Now.AddMinutes(1)));
    }

    [Fact]
    public void SkipRecordsTheReasonAndCompletes()
    {
        var row = NewRow();

        row.Skip(TaskNoticeSkipReasons.Resolved, Now);

        Assert.Equal(TaskNoticeEscalationStatus.Skipped, row.Status);
        Assert.Equal(TaskNoticeSkipReasons.Resolved, row.SkipReason);
        Assert.Equal(Now, row.CompletedAt);
        Assert.Throws<InvalidOperationException>(() => row.Skip(TaskNoticeSkipReasons.Read, Now));
    }

    [Fact]
    public void RecordAttemptAppendsToTheJsonLog()
    {
        var row = NewRow();

        row.RecordAttempt("whatsapp", "template_not_approved", Now);
        row.RecordAttempt("email", "sent", Now);

        Assert.Contains("template_not_approved", row.ChannelAttemptsJson, StringComparison.Ordinal);
        Assert.Contains("\"email\"", row.ChannelAttemptsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteByMarksTheChannelAndTheTime()
    {
        var row = NewRow();

        row.CompleteBy(TaskNoticeEscalationStatus.Email, Now);

        Assert.Equal(TaskNoticeEscalationStatus.Email, row.Status);
        Assert.Equal(Now, row.CompletedAt);
    }

    [Fact]
    public void FailEndsTheRowWithItsReason()
    {
        var row = NewRow();
        row.Claim(Now.AddMinutes(2));

        row.Fail(TaskNoticeSkipReasons.AttemptsExhausted, Now);

        Assert.Equal(TaskNoticeEscalationStatus.Failed, row.Status);
        Assert.Equal(TaskNoticeSkipReasons.AttemptsExhausted, row.SkipReason);
        Assert.Null(row.ClaimedUntil);
        Assert.Equal(Now, row.CompletedAt);
        Assert.Throws<InvalidOperationException>(() => row.Fail("again", Now));
    }
}
