using Modules.Notifications.Infrastructure.Channels;
using Modules.Notifications.Infrastructure.Messaging;

namespace Modules.Notifications.UnitTests;

/// <summary>El timeout del canal de Infobip (spec 2026-09-13, A5) y su relación con el lease del reclamo.</summary>
public sealed class InfobipEmailChannelTests
{
    // 30 s y no los 100 s implícitos de new HttpClient().
    [Fact]
    public void TheHttpClientGivesUpAfterThirtySeconds()
    {
        using var client = InfobipEmailChannel.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    // Si el lease venciera antes que el timeout, otra réplica retomaría un mensaje que todavía se está
    // enviando, y saldrían dos correos.
    [Fact]
    public void TheClaimLeaseOutlastsTheProviderTimeout()
    {
        Assert.True(
            OutboxDeliveryWorker.Lease > InfobipEmailChannel.RequestTimeout,
            $"Lease {OutboxDeliveryWorker.Lease} must be longer than the Infobip timeout {InfobipEmailChannel.RequestTimeout}.");
    }
}
