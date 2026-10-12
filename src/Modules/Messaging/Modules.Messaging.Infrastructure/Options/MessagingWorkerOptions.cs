namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §9: intervalos de entregas, medios y purga, con defaults en código.</summary>
public sealed class MessagingWorkerOptions
{
    public const string SectionName = "Messaging:Workers";

    public int DeliveryPollSeconds { get; set; } = 3;

    public int MediaPollSeconds { get; set; } = 5;

    /// <summary>Spec 2026-10-11 §6.3: el tick del worker de campañas, 2 s.</summary>
    public int CampaignPollSeconds { get; set; } = 2;

    public int PurgeIntervalHours { get; set; } = 24;

    public int DeliveryRetentionDays { get; set; } = 7;
}
