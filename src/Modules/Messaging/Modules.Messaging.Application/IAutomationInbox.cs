using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 §5.1: un entrante pendiente de procesar por el motor de chatbots. <c>Opened</c> (D-M16) dice que este
/// entrante abrió o reabrió la conversación; <c>Attempts</c> ya cuenta el reclamo que lo devolvió.</summary>
public sealed record AutomationInboxItem(Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts);

/// <summary><c>Stored</c>: la copia a R2 ya terminó; mientras sea <c>false</c> el medio todavía no se puede servir ni reenviar.</summary>
public sealed record InboundMediaForAutomation(string MimeType, string? FileName, bool Stored);

public sealed record InboundForAutomation(Guid MessageId, MessageKind Kind, string? Text, string? ReplyId, InboundMediaForAutomation? Media, DateTimeOffset OccurredAt);

/// <summary>Spec 2026-10-21 D-M1: la cola que la ingesta del webhook llena y el worker del motor vacía. El motor no corre dentro del
/// procesador del webhook porque éste no serializa por conversación y su reintento descarta el mismo wamid.</summary>
public interface IAutomationInbox
{
    // Complete y RetryLater toman la hora del reloj; Claim recibe `now` para que los leases y la regla «la más vieja de la conversación» se
    // comparen contra un solo instante, el que el worker usa en toda su pasada.
    /// <summary>Reclama hasta <paramref name="batch"/> filas vencidas con <c>SKIP LOCKED</c>, sólo la más vieja pendiente de cada
    /// conversación (el orden del cliente importa), y las deja en lease: 30 s el primer intento, 2 min el segundo y 10 min desde el tercero.</summary>
    Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken);

    // Complete y RetryLater reciben el Attempts del reclamo como testigo: si el motor se pasó de su lease, otra réplica ya volvió a
    // reclamar la fila (y Attempts avanzó), y el worker atrasado no puede cerrarla ni reprogramarla encima del que la tiene ahora.
    // Devuelven false cuando el testigo ya no coincide (o la fila ya está cerrada): lo que hizo ese worker ya no cuenta.

    /// <summary>Cierra la fila con el resultado; deja de reclamarse y deja de frenar a las más nuevas de su conversación.</summary>
    Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken);

    /// <summary>Vuelve a intentar tras <paramref name="delay"/>; con 5 intentos ya hechos la cierra con <c>failed</c> en vez de reprogramarla.</summary>
    Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>Lo que el motor necesita del mensaje; <c>null</c> si no existe en ese tenant. <c>Text</c> es el texto o, si no tiene, la
    /// leyenda del adjunto (P2-R4): una palabra clave también se dispara con la leyenda de una foto (D-C6, «cualquier entrante»).</summary>
    Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-21 D-M5: el entrante más reciente de la conversación (en el orden del hilo), con el que el traspaso del bot
    /// suena a todo el tenant como un entrante sin asignar; <c>null</c> si no tiene ninguno o no es del tenant.</summary>
    Task<Guid?> FindLastInboundIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Ruling P2-R7: borra hasta <paramref name="batch"/> filas ya procesadas antes de <paramref name="cutoff"/> y devuelve cuántas
    /// borró; una pendiente nunca, por vieja que sea. De todos los tenants: es mantenimiento del worker. Sin esto la tabla crece con cada
    /// entrante desde que el flag está prendido.</summary>
    Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken);
}
