using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>§6.4: auditoría atómica propia (proyección de audit.entries en MessagingDbContext, como
/// Integrations). No el IAuditRecorder compartido, ligado a TenancyDbContext. Un envío no se audita.</summary>
public interface IMessagingAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-11 §6.2: la auditoría de una campaña (creada, lanzada, cancelada, terminada o fallida). Las dos últimas las
    /// escribe el worker, sin una persona detrás: <paramref name="bySystem"/> y actor vacío, como Customers con la ingesta.</summary>
    void RecordCampaign(Guid tenantId, Guid actorId, bool bySystem, string action, Guid campaignId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-12 §3.3: crear, editar y borrar una respuesta rápida, en la misma transacción que el cambio.</summary>
    void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-15 §3.8: crear (sólo cuando el resolver lo crea), renombrar y archivar un motivo de resolución, en la misma
    /// transacción que el cambio.</summary>
    void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-21 §5: crear, editar, borrar, duplicar, volver predeterminado y reasignar conexiones de un horario de atención, en la
    /// misma transacción que el cambio. Siempre una persona.</summary>
    void RecordBusinessHours(Guid tenantId, Guid actorId, string action, Guid scheduleId, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-14 §4.6: crear, aprovisionar y borrar una plantilla, en la misma transacción que el espejo. Los cambios de
    /// estado que trae el webhook no se auditan (D-T14). <paramref name="changedFields"/> va a <c>changed_fields</c>: la adopción de una
    /// plantilla externa como de cotización manda <c>["kind"]</c>; <c>null</c> es <c>[]</c>, como el resto del módulo.</summary>
    void RecordTemplate(
        Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt, IReadOnlyCollection<string>? changedFields = null);
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
    public const string AutomationPaused = "messaging.conversation.automation_paused";
    public const string AutomationResumed = "messaging.conversation.automation_resumed";

    /// <summary>Spec 2026-10-11 decisión 9: una conversación abierta desde la plataforma, asignada a quien la inició.</summary>
    public const string Started = "messaging.conversation.started";

    /// <summary>La acción de auditoría de un evento de asignación o de asistente: un cambio puede escribir varios eventos
    /// (tomar una conversación con asistente activo escribe <c>Taken</c> y <c>AutomationPaused</c>) y cada uno tiene la suya.</summary>
    public static string ForEvent(ConversationEventType type) => type switch
    {
        ConversationEventType.Taken => Taken,
        ConversationEventType.Transferred => Transferred,
        ConversationEventType.Released => Released,
        ConversationEventType.AutoTaken => AutoTaken,
        ConversationEventType.AutomationPaused => AutomationPaused,
        ConversationEventType.AutomationResumed => AutomationResumed,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "That event type has no assignment audit action."),
    };

    public const string CampaignResourceType = "campaign";
    public const string CampaignCreated = "messaging.campaign.created";
    public const string CampaignLaunched = "messaging.campaign.launched";
    public const string CampaignCancelled = "messaging.campaign.cancelled";
    public const string CampaignRetried = "messaging.campaign.retried";
    public const string CampaignCompleted = "messaging.campaign.completed";
    public const string CampaignFailed = "messaging.campaign.failed";

    public const string QuickReplyResourceType = "quick_reply";
    public const string QuickReplyCreated = "messaging.quick_reply.created";
    public const string QuickReplyUpdated = "messaging.quick_reply.updated";
    public const string QuickReplyDeleted = "messaging.quick_reply.deleted";
    public const string ResolutionReasonResourceType = "resolution_reason";
    public const string ResolutionReasonCreated = "messaging.resolution_reason.created";
    public const string ResolutionReasonRenamed = "messaging.resolution_reason.renamed";
    public const string ResolutionReasonArchived = "messaging.resolution_reason.archived";

    public const string BusinessHoursResourceType = "business_hours_schedule";
    public const string BusinessHoursCreated = "messaging.business_hours.created";
    public const string BusinessHoursUpdated = "messaging.business_hours.updated";
    public const string BusinessHoursDeleted = "messaging.business_hours.deleted";
    public const string BusinessHoursDuplicated = "messaging.business_hours.duplicated";
    public const string BusinessHoursDefaultSet = "messaging.business_hours.default_set";
    public const string BusinessHoursConnectionsAssigned = "messaging.business_hours.connections_assigned";

    public const string TemplateResourceType = "message_template";
    public const string TemplateCreated = "messaging.template.created";
    public const string TemplateQuotationProvisioned = "messaging.template.quotation_provisioned";
    public const string TemplateDeleted = "messaging.template.deleted";
}
