using Modules.Notifications.Application;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateEmailStartsPending()
    {
        var notification = Notification.CreateEmail(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "person@example.com",
            InvitationEmailTemplate.TemplateRef,
            Now);

        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(NotificationChannel.Email, notification.Channel);
        Assert.Null(notification.SentAt);
    }

    [Fact]
    public void MarkSentAndMarkFailedTransition()
    {
        var notification = Notification.CreateEmail(
            Guid.CreateVersion7(), Guid.CreateVersion7(),
            "person@example.com", "t", Now);

        notification.MarkSent(Now);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(Now, notification.SentAt);

        notification.MarkFailed(new string('x', 800), Now);
        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Null(notification.SentAt);
        Assert.Equal(500, notification.FailureReason!.Length);
    }

    [Fact]
    public void InvitationTemplateRendersRecipientAndLoginUrl()
    {
        var message = InvitationEmailTemplate.Render(
            "person@example.com",
            Guid.CreateVersion7(),
            "http://localhost:3002/login");

        Assert.Equal("person@example.com", message.ToAddress);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.Contains("http://localhost:3002/login", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("http://localhost:3002/login", message.TextBody, StringComparison.Ordinal);
    }
}
