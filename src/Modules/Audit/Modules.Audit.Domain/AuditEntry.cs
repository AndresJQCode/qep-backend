namespace Modules.Audit.Domain;

/// <summary>
/// An append-only record of an audited action: who did what, on which resource, in which
/// tenant, with what outcome. Created through <see cref="Create"/> and never mutated
/// afterwards. The queryable audit store owned by the Audit capability (schema
/// <c>audit</c>); producer modules write entries atomically within their own transaction
/// (ADR 0019, atomic path) or via the audit outbox projection (operational path).
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
