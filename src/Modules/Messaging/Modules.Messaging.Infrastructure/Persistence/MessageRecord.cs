using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La fila de <c>messaging.messages</c> para leer con EF. Se escribe por SQL (ingesta y envío);
/// por eso no es un agregado: sin reglas, sin setters privados que esconder.</summary>
internal sealed class MessageRecord
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageKind Kind { get; set; }
    public MessageStatus Status { get; set; }
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public string? Details { get; set; }
    public string? Wamid { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SentByMemberId { get; set; }
    public int? FailureCode { get; set; }
    public string? FailureTitle { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public MessageMediaRecord? Media { get; set; }
}

internal sealed class MessageMediaRecord
{
    public Guid MessageId { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string MetaMediaId { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? StorageKey { get; set; }
    public DateTimeOffset? StoredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class WebhookDeliveryRecord
{
    public long Id { get; set; }
    public byte[] BodySha256 { get; set; } = [];
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
