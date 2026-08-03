namespace Modules.Notifications.Domain;

/// <summary>
/// A record of an outbound communication request processed by the Notifications
/// capability. Created in <see cref="NotificationStatus.Pending"/> and transitioned
/// to <see cref="NotificationStatus.Sent"/> or <see cref="NotificationStatus.Failed"/>
/// by the delivery worker. Idempotent processing is guarded by the inbox, not by this
/// aggregate.
/// </summary>
public sealed class Notification
{
    private Notification()
    {
    }

    private Notification(
        NotificationId id,
        Guid tenantId,
        Guid recipientId,
        string recipientAddress,
        NotificationChannel channel,
        string templateRef,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        RecipientId = recipientId;
        RecipientAddress = recipientAddress;
        Channel = channel;
        TemplateRef = templateRef;
        Status = NotificationStatus.Pending;
        CreatedAt = createdAt;
    }

    public NotificationId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RecipientId { get; private set; }

    public string RecipientAddress { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public string TemplateRef { get; private set; } = string.Empty;

    public NotificationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? FailureReason { get; private set; }

    public static Notification CreateEmail(
        Guid tenantId,
        Guid recipientId,
        string recipientAddress,
        string templateRef,
        DateTimeOffset createdAt) =>
        new(
            NotificationId.New(),
            tenantId,
            recipientId,
            recipientAddress,
            NotificationChannel.Email,
            templateRef,
            createdAt);

    public void MarkSent(DateTimeOffset occurredAt)
    {
        Status = NotificationStatus.Sent;
        SentAt = occurredAt;
        FailureReason = null;
    }

    public void MarkFailed(string reason, DateTimeOffset occurredAt)
    {
        Status = NotificationStatus.Failed;
        SentAt = null;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }
}
