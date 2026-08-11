namespace Modules.Audit.Domain;

/// <summary>
/// Un registro append-only de una acción auditada: quién hizo qué, sobre qué recurso, en
/// qué tenant y con qué resultado. Se crea con <see cref="Create"/> y nunca se muta
/// después. Es el almacén de auditoría consultable de la capacidad Audit (esquema
/// <c>audit</c>); los módulos productores escriben entradas atómicamente dentro de su
/// propia transacción (ADR 0019, camino atómico) o vía la proyección del outbox (operativo).
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
    }

    private AuditEntry(
        AuditEntryId id,
        Guid? tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        string changedFieldsJson,
        string source,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        ActorId = actorId;
        ActorType = actorType;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Outcome = outcome;
        ChangedFieldsJson = changedFieldsJson;
        Source = source;
        OccurredAt = occurredAt;
    }

    public AuditEntryId Id { get; private init; }

    public Guid? TenantId { get; private init; }

    public Guid ActorId { get; private init; }

    public AuditActorType ActorType { get; private init; }

    public string Action { get; private init; } = string.Empty;

    public string ResourceType { get; private init; } = string.Empty;

    public string ResourceId { get; private init; } = string.Empty;

    public string Outcome { get; private init; } = string.Empty;

    public string ChangedFieldsJson { get; private init; } = "[]";

    public string Source { get; private init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private init; }

    public static AuditEntry Create(
        Guid? tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        string changedFieldsJson,
        string source,
        DateTimeOffset occurredAt) =>
        new(
            AuditEntryId.New(),
            tenantId,
            actorId,
            actorType,
            action,
            resourceType,
            resourceId,
            outcome,
            string.IsNullOrWhiteSpace(changedFieldsJson) ? "[]" : changedFieldsJson,
            source,
            occurredAt);
}
