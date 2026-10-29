using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para Messaging, copia de IntegrationsAuditRecorder: la entrada
// se acumula en MessagingDbContext y commitea o revierte con el cambio de la conversación. audit.entries
// es del módulo Audit; acá se proyecta ExcludeFromMigrations. Resolver y reabrir no cambian campos
// editables, así que changed_fields va vacío.
internal sealed class MessagingAuditRecorder(MessagingDbContext dbContext) : IMessagingAuditRecorder
{
    private const string Source = "messaging";
    private const string Outcome = "success";
    private const string NoChangedFields = "[]";

    public void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.ResourceType,
            conversationId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-21 §5.5: lo que hace el motor de chatbots no tiene persona detrás: actor vacío y System, como RecordCampaign(bySystem: true).
    public void RecordBySystem(Guid tenantId, string action, Guid conversationId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            Guid.Empty,
            AuditActorType.System,
            action,
            MessagingAuditActions.ResourceType,
            conversationId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-11 §6.2: la campaña se audita en la misma transacción que su cambio. Las que escribe el worker (terminada, fallida) no
    // tienen una persona detrás: actor System y vacío, como Customers con lo que nace de un webhook.
    public void RecordCampaign(Guid tenantId, Guid actorId, bool bySystem, string action, Guid campaignId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            bySystem ? AuditActorType.System : AuditActorType.Human,
            action,
            MessagingAuditActions.CampaignResourceType,
            campaignId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-12 §3.3: siempre una persona (quien gestiona en Settings). changed_fields vacío, como el resto del módulo.
    public void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.QuickReplyResourceType,
            quickReplyId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-29: el orden personal; el recurso es la membresía cuyo orden cambió. Siempre una persona, changed_fields vacío.
    public void RecordQuickReplyOrder(Guid tenantId, Guid actorId, Guid memberId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            MessagingAuditActions.QuickReplyOrderSaved,
            MessagingAuditActions.QuickReplyOrderResourceType,
            memberId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-15 §3.8: siempre una persona (quien resuelve o quien gestiona en Settings). changed_fields vacío, como el resto.
    public void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.ResolutionReasonResourceType,
            reasonId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-21 §5: siempre una persona (el admin en la pantalla de horarios). changed_fields vacío, como el resto del módulo.
    public void RecordBusinessHours(Guid tenantId, Guid actorId, string action, Guid scheduleId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.BusinessHoursResourceType,
            scheduleId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));

    // Spec 2026-10-14 §4.6: siempre una persona (el admin en la pantalla de plantillas). changed_fields vacío salvo que quien llama
    // diga qué cambió (la adopción manda ["kind"]).
    public void RecordTemplate(
        Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt, IReadOnlyCollection<string>? changedFields = null) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            MessagingAuditActions.TemplateResourceType,
            templateId.ToString(),
            Outcome,
            changedFields is { Count: > 0 } ? JsonSerializer.Serialize(changedFields) : NoChangedFields,
            Source,
            occurredAt));
}
