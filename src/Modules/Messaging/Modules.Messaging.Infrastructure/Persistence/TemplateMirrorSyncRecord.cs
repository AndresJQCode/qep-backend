namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.1 <c>messaging.template_mirror_syncs</c>: sin esta fila no se distingue «un WABA sin plantillas» de «un
/// espejo que nunca se llenó», y la segunda situación obliga a sincronizar antes de servir el catálogo de envío.</summary>
internal sealed class TemplateMirrorSyncRecord
{
    public Guid TenantId { get; set; }

    public Guid ConnectionId { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
