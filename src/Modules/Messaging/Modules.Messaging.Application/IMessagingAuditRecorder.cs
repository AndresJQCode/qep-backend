namespace Modules.Messaging.Application;

/// <summary>§6.4: auditoría atómica propia (proyección de audit.entries en MessagingDbContext, como
/// Integrations). No el IAuditRecorder compartido, ligado a TenancyDbContext. Un envío no se audita.</summary>
public interface IMessagingAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-11 §6.2: la auditoría de una campaña (creada, lanzada, cancelada, terminada o fallida). Las dos últimas las
    /// escribe el worker, sin una persona detrás: <paramref name="bySystem"/> y actor vacío, como Customers con la ingesta.</summary>
    void RecordCampaign(Guid tenantId, Guid actorId, bool bySystem, string action, Guid campaignId, DateTimeOffset occurredAt);
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

    /// <summary>Spec 2026-10-11 decisión 9: una conversación abierta desde la plataforma, asignada a quien la inició.</summary>
    public const string Started = "messaging.conversation.started";

    public const string CampaignResourceType = "campaign";
    public const string CampaignCreated = "messaging.campaign.created";
    public const string CampaignLaunched = "messaging.campaign.launched";
    public const string CampaignCancelled = "messaging.campaign.cancelled";
    public const string CampaignCompleted = "messaging.campaign.completed";
    public const string CampaignFailed = "messaging.campaign.failed";
}
