namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §9 y §8.2.</summary>
public sealed class MessagingWebhookOptions
{
    public const string SectionName = "Messaging:Webhook";

    /// <summary>D-M1: 4 MiB (Meta documenta hasta 3 MB).</summary>
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;

    public int ConcurrencyLimit { get; set; } = 64;

    public int QueueLimit { get; set; } = 256;
}
