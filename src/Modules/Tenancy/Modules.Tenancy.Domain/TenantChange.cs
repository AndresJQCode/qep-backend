namespace Modules.Tenancy.Domain;

/// <summary>
/// Una fila del historial único de un tenant (spec 2026-10-08 §3, D12): un cambio de módulo o de estado
/// del tenant, agrupado con los demás de su operación por <see cref="BatchId"/>. Inmutable: no hay
/// camino para editarla ni borrarla. <see cref="FromStatus"/>/<see cref="ToStatus"/> son texto porque
/// guardan dos vocabularios: <c>active</c>/<c>inactive</c> para módulos y el nombre del enum
/// (<c>Active</c>/<c>Suspended</c>) para el tenant, como <c>tenants.status</c>.
/// </summary>
public sealed class TenantChange
{
    public const int NoteMaxLength = 300;
    public const int StatusMaxLength = 20;

    private TenantChange()
    {
        ToStatus = string.Empty;
    }

    private TenantChange(
        TenantId tenantId, Guid batchId, TenantChangeKind kind, TenantModuleKey? moduleKey,
        string? fromStatus, string toStatus, ChangeReason reason, string? note, Guid actorUserId,
        DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        BatchId = batchId;
        Kind = kind;
        ModuleKey = moduleKey;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ActorUserId = actorUserId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public TenantChangeKind Kind { get; private set; }
    public TenantModuleKey? ModuleKey { get; private set; }
    public string? FromStatus { get; private set; }
    public string ToStatus { get; private set; }
    public ChangeReason Reason { get; private set; }
    public string? Note { get; private set; }
    public Guid ActorUserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <param name="fromStatus">null = la fila del módulo no existía.</param>
    public static TenantChange ForModule(
        TenantId tenantId, Guid batchId, TenantModuleKey moduleKey, TenantModuleStatus? fromStatus,
        TenantModuleStatus toStatus, ChangeReason reason, string? note, Guid actorUserId, DateTimeOffset occurredAt) =>
        new(tenantId, batchId, TenantChangeKind.Module, moduleKey,
            fromStatus is { } from ? TenantChangeVocabulary.ToText(from) : null,
            TenantChangeVocabulary.ToText(toStatus), reason, note, actorUserId, occurredAt);

    public static TenantChange ForTenantStatus(
        TenantId tenantId, Guid batchId, TenantStatus fromStatus, TenantStatus toStatus, ChangeReason reason,
        string? note, Guid actorUserId, DateTimeOffset occurredAt) =>
        new(tenantId, batchId, TenantChangeKind.TenantStatus, null, fromStatus.ToString(), toStatus.ToString(),
            reason, note, actorUserId, occurredAt);
}
