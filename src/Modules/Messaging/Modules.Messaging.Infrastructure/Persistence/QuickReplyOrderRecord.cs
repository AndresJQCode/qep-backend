namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-29: <c>messaging.quick_reply_orders</c>, el orden personal de las respuestas rápidas de un miembro. Una fila por
/// <c>(tenant_id, member_id)</c> con los ids en orden (<c>uuid[]</c>). Sin FK a <c>quick_replies</c>: un borrado deja el id en el arreglo
/// y la lectura lo salta. <c>member_id</c> es referencia blanda a la membresía de Tenancy (otro schema), como <c>assigned_member_id</c>.
/// Se escribe por SQL (upsert); EF sólo la lee.</summary>
internal sealed class QuickReplyOrderRecord
{
    public Guid TenantId { get; set; }

    public Guid MemberId { get; set; }

    public List<Guid> QuickReplyIds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
