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
    public void InvitationTemplateRendersRecipientAndInvitationLink()
    {
        var message = InvitationEmailTemplate.Render(
            "person@example.com",
            "http://localhost:3002/invitations/abc-123");

        Assert.Equal("person@example.com", message.ToAddress);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.Contains(
            "http://localhost:3002/invitations/abc-123",
            message.HtmlBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "http://localhost:3002/invitations/abc-123",
            message.TextBody,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Las plantillas publicadas son inmutables y la referencia es la que audita qué se le
    /// mandó a quién: cambiar el contenido (el link ahora lleva el token de invitación, no
    /// la pantalla de login) exige subir la versión, no reusar la v1.
    /// </summary>
    [Fact]
    public void InvitationTemplateRefIsVersionTwo()
    {
        Assert.Equal("identity.invitation.v2", InvitationEmailTemplate.TemplateRef);
    }
}
