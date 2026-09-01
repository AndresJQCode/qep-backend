using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class InvitationLinkTests
{
    [Fact]
    public void ComposeAppendsTheTokenAsAPathSegment()
    {
        Assert.Equal(
            "http://localhost:3002/invitations/tok-123",
            InvitationLink.Compose("http://localhost:3002/invitations", "tok-123"));
    }

    // Una barra final en la configuración no puede producir "//token": el frontend rutea
    // por path exacto y un segmento vacío rompe el deep-link del email.
    [Fact]
    public void ComposeToleratesATrailingSlashInTheConfiguredBase()
    {
        Assert.Equal(
            "http://localhost:3002/invitations/tok-123",
            InvitationLink.Compose("http://localhost:3002/invitations/", "tok-123"));
    }
}
