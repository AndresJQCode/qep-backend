using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §9 y 2026-10-11 §6.3. El tick de campañas por defecto es costo en reposo contra una base
/// compartida y sólo retrasa el arranque de una campaña programada (<c>DrainAsync</c> sostiene el caudal), así que arranca
/// en 10 s. Entregas y medios no cambian.</summary>
public sealed class MessagingWorkerOptionsTests
{
    [Fact]
    public void TheDefaultCampaignPollIsTenSeconds() =>
        Assert.Equal(10, new MessagingWorkerOptions().CampaignPollSeconds);

    /// <summary>Spec 2026-11-09 «Worker y ejecución»: el tick de programados, 5 s por defecto.</summary>
    [Fact]
    public void TheDefaultScheduledPollIsFiveSeconds() =>
        Assert.Equal(5, new MessagingWorkerOptions().ScheduledPollSeconds);

    [Fact]
    public void DeliveryAndMediaPollsKeepTheirDefaults()
    {
        var options = new MessagingWorkerOptions();

        Assert.Equal(3, options.DeliveryPollSeconds);
        Assert.Equal(5, options.MediaPollSeconds);
    }
}
