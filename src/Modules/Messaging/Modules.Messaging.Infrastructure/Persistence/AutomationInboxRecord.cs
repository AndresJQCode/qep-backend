namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-21 §4.2: un entrante pendiente de que el motor de chatbots lo procese. La ingesta lo inserta en la misma
/// transacción que el mensaje; clave por <c>message_id</c> para que reintentar la ingesta no lo duplique.</summary>
internal sealed class AutomationInboxRecord
{
    public Guid MessageId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>D-M16: este entrante abrió o reabrió la conversación.</summary>
    public bool Opened { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public short Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Outcome { get; set; }
}
