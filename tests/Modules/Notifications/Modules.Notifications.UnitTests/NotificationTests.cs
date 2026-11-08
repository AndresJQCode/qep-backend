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
    public void CreateEmailAcceptsANullTenant()
        => Assert.Null(Notification.CreateEmail(
            null, Guid.NewGuid(), "a@x.com", "identity.password-reset.v1", Now).TenantId);

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
    public void AnonymizeRecipientClearsTheAddressAndTheRecipientButKeepsTheStatus()
    {
        var notification = Notification.CreateEmail(
            null, Guid.CreateVersion7(), "person@example.com", "buyers.verification.v1", Now);
        notification.MarkSent(Now);

        notification.AnonymizeRecipient();

        Assert.Equal(Guid.Empty, notification.RecipientId);
        Assert.Equal(string.Empty, notification.RecipientAddress);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal("buyers.verification.v1", notification.TemplateRef);
    }

    [Fact]
    public void EveryBuyerTemplateSharesThePrefixTheDeletionAnonymizes()
    {
        string[] refs =
        [
            BuyerVerificationEmailTemplate.TemplateRef,
            BuyerExistingAccountEmailTemplate.TemplateRef,
            BuyerPasswordResetEmailTemplate.TemplateRef,
            BuyerAccountDeletedEmailTemplate.TemplateRef,
        ];

        Assert.All(refs, value => Assert.StartsWith(
            BuyerAccountDeletedEmailTemplate.BuyerTemplateRefPrefix, value, StringComparison.Ordinal));
    }

    [Fact]
    public void InvitationTemplateRendersRecipientAndInvitationLink()
    {
        var message = InvitationEmailTemplate.Render(
            "person@example.com",
            "http://localhost:3002/invitations/abc-123",
            "Origen Botánico");

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
    /// mandó a quién: cambiar el contenido exige subir la versión, no reusar la anterior. La v2
    /// cambió el link de la pantalla de login por el deep-link con el token; la v3 nombra al
    /// tenant en el asunto y en el cuerpo; la v4 ofrece Google o una contraseña.
    /// </summary>
    [Fact]
    public void InvitationTemplateRefIsVersionFour()
    {
        Assert.Equal("identity.invitation.v4", InvitationEmailTemplate.TemplateRef);
    }
}
