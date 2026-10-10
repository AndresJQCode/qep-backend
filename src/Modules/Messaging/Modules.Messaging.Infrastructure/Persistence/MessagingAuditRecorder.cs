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
}
