namespace Modules.Messaging.Application;

/// <summary>§6.4: auditoría atómica propia (proyección de audit.entries en MessagingDbContext, como
/// Integrations). No el IAuditRecorder compartido, ligado a TenancyDbContext. Un envío no se audita.</summary>
public interface IMessagingAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt);
}

public static class MessagingAuditActions
{
    public const string ResourceType = "conversation";
    public const string Resolved = "messaging.conversation.resolved";
    public const string Reopened = "messaging.conversation.reopened";
    public const string Taken = "messaging.conversation.taken";
    public const string Transferred = "messaging.conversation.transferred";
    public const string Released = "messaging.conversation.released";
    public const string AutoTaken = "messaging.conversation.auto_taken";
}
