using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que uno inexistente.</summary>
public interface IConversationRepository
{
    /// <summary>Con tracking: para resolver y reabrir (§8.5).</summary>
    Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>§8.4: una sola sentencia que se commitea sola: <c>unread_count = 0</c>, <c>updated_at</c> y
    /// <c>version + 1</c>, sólo si había no leídos. Sin token de concurrencia: la ingesta sube
    /// <c>version</c> por SQL (§7.5), y cargar y guardar el agregado convertiría un read en 412. <c>null</c>
    /// si no tocó nada (ya estaba en 0, o no existe en el tenant).</summary>
    Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §3.2: el espejo de <see cref="MarkReadAsync"/>: <c>unread_count = 1</c>,
    /// <c>updated_at</c> y <c>version + 1</c>, sólo si estaba en 0. Misma sentencia única y sin token: pisar un 3
    /// con un 1 mentiría sobre cuántos entrantes hay. <c>true</c> si tocó la fila.</summary>
    Task<bool> MarkUnreadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §7.1: <c>unread_count = GREATEST(unread_count, entrantes desde el mensaje)</c>, con
    /// «desde» en el orden del hilo (<c>occurred_at, id</c>) y sólo <c>Inbound</c>. Un UPDATE, sin token, como los
    /// otros dos. El conteo se hace dentro del UPDATE: una ingesta concurrente espera y suma después.</summary>
    Task<UnreadFromOutcome> MarkUnreadFromAsync(Guid tenantId, Guid conversationId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Para distinguir «ya estaba leída» (204) de «no existe en el tenant» (404) cuando
    /// <see cref="MarkReadAsync"/> o <see cref="MarkUnreadAsync"/> no tocaron nada.</summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.5 y §3 (corrección 7): un UPDATE condicional (<c>assigned_member_id IS NULL</c>) en su
    /// propia transacción corta, antes del reclamo largo del envío: así la ingesta de esta conversación nunca espera a
    /// Meta, y sin token de concurrencia un entrante que subió la versión no lo convierte en 412 (RF7).</summary>
    Task<AutoAssignOutcome> TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-10 §8.5 paso 5.</summary>
public enum AutoAssignOutcome
{
    /// <summary>Estaba sin asignar y ahora es de quien envía (evento AutoTaken y auditoría ya commiteados).</summary>
    Assigned,

    /// <summary>Ya era de quien envía.</summary>
    AlreadyMine,

    /// <summary>Otra persona la tomó primero (§9.3).</summary>
    AssignedToOther,
}

/// <summary>Spec 2026-10-14 §7.1.</summary>
public enum UnreadFromOutcome
{
    /// <summary>Subió el contador.</summary>
    Marked,

    /// <summary>Ya había tantos o más no leídos que los que pide la marca.</summary>
    Unchanged,

    /// <summary>El mensaje no es de esa conversación en ese tenant (o la conversación no existe).</summary>
    MessageNotFound,
}

/// <summary>Lo que el read devuelve de la fila que marcó: lo justo para el acuse a Meta (§8.4).</summary>
/// <param name="Channel">Spec 2026-10-14 decisión 14: en Instagram el acuse es por persona (<paramref name="UserId"/>, el IGSID).</param>
public sealed record ReadReceiptTarget(
    Guid ConnectionId, string? LastInboundWamid, DateTimeOffset? LastInboundAt,
    ConversationChannel Channel = ConversationChannel.WhatsApp, string? UserId = null);

/// <summary>Guarda el agregado y su auditoría en una transacción; traduce la concurrencia a
/// <c>concurrency.conflict</c> en Infrastructure.</summary>
public interface IMessagingUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
