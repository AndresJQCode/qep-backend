using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09 «Transiciones»: el agregado <see cref="ScheduledMessage"/> y sus predicados. Los guardas lanzan
/// <see cref="InvalidOperationException"/>; el handler consulta <c>CanEdit</c>/<c>CanRetry</c> antes y responde el 409.</summary>
public sealed class ScheduledMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Editor = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static ScheduledMessage Text(string text = "Hola") =>
        ScheduledMessage.CreateText(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), text, Now.AddHours(2), Author, Now);

    private static ScheduledMessage Media() =>
        ScheduledMessage.CreateMedia(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), MessageKind.Image, false, "pie",
            new ScheduledMedia("k/1", "image/png", "a.png", 10, null), Now.AddHours(2), Author, Now);

    private static ScheduledMessage InStatus(ScheduledMessageStatus status)
    {
        var message = Text();
        switch (status)
        {
            case ScheduledMessageStatus.Scheduled:
                break;
            case ScheduledMessageStatus.Failed:
                message.MarkFailed("messaging.scheduled.unknown_outcome", Now);
                break;
            case ScheduledMessageStatus.Cancelled:
                message.Cancel(Author, Now);
                break;
            case ScheduledMessageStatus.Sending:
                message.MarkSending(Now);
                break;
            case ScheduledMessageStatus.Sent:
                message.MarkSending(Now);
                message.MarkSent(Guid.CreateVersion7(), Now);
                break;
        }

        return message;
    }

    [Fact]
    public void CreateTextStartsScheduledAtVersionOne()
    {
        var message = Text("  Hola  ");

        Assert.Equal(ScheduledMessageStatus.Scheduled, message.Status);
        Assert.Equal(MessageKind.Text, message.Kind);
        Assert.Equal("Hola", message.Text);
        Assert.Equal(Author, message.AuthorMemberId);
        Assert.Equal(Author, message.CreatedByMemberId);
        Assert.Equal(1, message.Version);
        Assert.Equal(0, message.Attempts);
        Assert.True(message.IsActive);
        Assert.Null(message.UpdatedAt);
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Sending)]
    [InlineData(ScheduledMessageStatus.Sent)]
    [InlineData(ScheduledMessageStatus.Failed)]
    [InlineData(ScheduledMessageStatus.Cancelled)]
    public void OnlyAScheduledMessageCanBeEdited(ScheduledMessageStatus status)
    {
        var message = InStatus(status);

        Assert.False(message.CanEdit);
        Assert.Throws<InvalidOperationException>(() => message.Cancel(Editor, Now));
        Assert.Throws<InvalidOperationException>(() => message.SendNow(Now));
        Assert.Throws<InvalidOperationException>(() => message.Edit(Now.AddHours(3), null, false, null, false, Editor, Now));
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Scheduled)]
    [InlineData(ScheduledMessageStatus.Sending)]
    [InlineData(ScheduledMessageStatus.Sent)]
    [InlineData(ScheduledMessageStatus.Cancelled)]
    public void OnlyAFailedMessageCanBeRetried(ScheduledMessageStatus status)
    {
        var message = InStatus(status);

        Assert.False(message.CanRetry);
        Assert.False(message.CanDiscard);
        Assert.Throws<InvalidOperationException>(() => message.Retry(Now));
        Assert.Throws<InvalidOperationException>(() => message.Discard(Editor, Now));
    }

    [Fact]
    public void EveryTransitionBumpsVersion()
    {
        var sendNow = Text();
        sendNow.SendNow(Now);
        Assert.Equal(2, sendNow.Version);

        var cancel = Text();
        cancel.Cancel(Editor, Now);
        Assert.Equal(2, cancel.Version);
        Assert.Equal(Editor, cancel.CancelledByMemberId);
        Assert.Equal(Now, cancel.CancelledAt);
        Assert.Equal(ScheduledMessageStatus.Cancelled, cancel.Status);
        Assert.False(cancel.IsActive);

        var retry = InStatus(ScheduledMessageStatus.Failed);
        var failedVersion = retry.Version;
        retry.Retry(Now);
        Assert.Equal(failedVersion + 1, retry.Version);

        var discard = InStatus(ScheduledMessageStatus.Failed);
        failedVersion = discard.Version;
        discard.Discard(Editor, Now);
        Assert.Equal(failedVersion + 1, discard.Version);
        Assert.Equal(Editor, discard.CancelledByMemberId);
        Assert.Equal(ScheduledMessageStatus.Cancelled, discard.Status);
    }

    [Fact]
    public void EditingOnlyScheduledAtKeepsTheAuthor()
    {
        var message = Text();

        var result = message.Edit(Now.AddHours(3), null, false, null, false, Editor, Now.AddMinutes(5));

        Assert.Equal(Author, message.AuthorMemberId);
        Assert.Equal(["scheduledAt"], result.ChangedFields);
        Assert.Equal(Now.AddHours(3), message.ScheduledAt);
        Assert.Equal(Editor, message.UpdatedByMemberId);
        Assert.Equal(Now.AddMinutes(5), message.UpdatedAt);
    }

    [Fact]
    public void EditingTheTextChangesTheAuthor()
    {
        var message = Text();

        var result = message.Edit(null, "  Otro  ", true, null, false, Editor, Now);

        Assert.Equal(Editor, message.AuthorMemberId);
        Assert.Equal(["text"], result.ChangedFields);
        Assert.Equal("Otro", message.Text);
        Assert.Equal(2, message.Version);
    }

    [Fact]
    public void AnIdenticalEditChangesNothing()
    {
        var message = Text("Hola");

        var result = message.Edit(message.ScheduledAt, "Hola", true, null, false, Editor, Now.AddMinutes(1));

        Assert.Empty(result.ChangedFields);
        Assert.Equal(1, message.Version);
        Assert.Null(message.UpdatedAt);
        Assert.Null(message.UpdatedByMemberId);
        Assert.Equal(Author, message.AuthorMemberId);
    }

    [Fact]
    public void ANullScheduledAtKeepsTheStoredTime()
    {
        var message = Text();
        var stored = message.ScheduledAt;

        var result = message.Edit(null, "x", true, null, false, Editor, Now);

        Assert.Equal(stored, message.ScheduledAt);
        Assert.Equal(["text"], result.ChangedFields);
    }

    [Fact]
    public void AnEmptyCaptionClearsIt()
    {
        var message = Media();

        var result = message.Edit(null, null, false, "  ", true, Editor, Now);

        Assert.Null(message.Caption);
        Assert.Equal(["caption"], result.ChangedFields);
        Assert.Equal(Editor, message.AuthorMemberId);
    }

    [Fact]
    public void RetryReschedulesNowAndClearsTheFailure()
    {
        var message = InStatus(ScheduledMessageStatus.Failed);
        Assert.Equal("messaging.scheduled.unknown_outcome", message.FailureCode);
        Assert.True(message.CanRetry);
        Assert.True(message.CanDiscard);
        Assert.True(message.IsActive);

        message.Retry(Now.AddMinutes(30));

        Assert.Equal(ScheduledMessageStatus.Scheduled, message.Status);
        Assert.Equal(Now.AddMinutes(30), message.ScheduledAt);
        Assert.Null(message.FailureCode);
    }
}
