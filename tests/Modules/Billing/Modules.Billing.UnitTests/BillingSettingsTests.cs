using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class BillingSettingsTests
{
    [Fact]
    public void ChangeConnectionBumpsVersionAndAllowsClearing()
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var settings = BillingSettings.Create(Guid.NewGuid(), now);
        Assert.Null(settings.WhatsAppConnectionId);
        var version = settings.Version;
        var connection = Guid.NewGuid();

        settings.ChangeWhatsAppConnection(connection, now.AddMinutes(1));
        Assert.Equal(connection, settings.WhatsAppConnectionId);

        settings.ChangeWhatsAppConnection(null, now.AddMinutes(2));
        Assert.Null(settings.WhatsAppConnectionId);
        Assert.Equal(version + 2, settings.Version);
    }
}
