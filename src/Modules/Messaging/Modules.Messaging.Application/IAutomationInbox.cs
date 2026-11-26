using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-03: qué trae una fila de la cola. <c>Engine</c> es lo de siempre (un entrante o un arranque manual, que el motor
/// procesa); <c>ReleaseRun</c> es la señal de que una persona tomó la conversación y la corrida viva del bot tiene que cerrarse.</summary>
public static class AutomationInboxKinds
{
    public const string Engine = "Engine";
    public const string ReleaseRun = "ReleaseRun";

    /// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: el chatbot delegó la conversación; la reclama el procesador de agentes
    /// (<c>bot_kind = 2</c>) para abrir la sesión del bloque.</summary>
    public const string Delegated = "Delegated";

    /// <summary>Spec 2026-11-17 §6.3: el agente devolvió la conversación; la reclama el motor de chatbots (<c>bot_kind = 1</c>) para seguir
    /// el flujo por la salida del resultado.</summary>
    public const string AgentReturned = "AgentReturned";
}

/// <summary>Spec 2026-10-21 §5.1: un entrante pendiente de procesar por el motor de chatbots. <c>Opened</c> (D-M16) dice que este
/// entrante abrió o reabrió la conversación; <c>Attempts</c> ya cuenta el reclamo que lo devolvió. <c>Kind</c> (spec 2026-11-03) es uno de
/// <see cref="AutomationInboxKinds"/>. <c>DelegationId</c> (spec 2026-11-17 §5.3) es la delegación de una fila
/// <see cref="AutomationInboxKinds.Delegated"/> o <see cref="AutomationInboxKinds.AgentReturned"/>, y <c>null</c> en las demás; en esas
/// dos <c>MessageId</c> es un id propio de la fila, sin mensaje detrás.</summary>
public sealed record AutomationInboxItem(
    Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts,
    string Kind = AutomationInboxKinds.Engine, Guid? DelegationId = null);

/// <summary><c>Stored</c>: la copia a R2 ya terminó; mientras sea <c>false</c> el medio todavía no se puede servir ni reenviar.</summary>
public sealed record InboundMediaForAutomation(string MimeType, string? FileName, bool Stored);

/// <summary><c>ReplyToMessageId</c>: el mensaje que cita (<c>reply_to_message_id</c>, el id de QEP del <c>context.id</c> de Meta), o <c>null</c>.
/// <c>ConversationId</c>: la conversación del entrante (spec 2026-10-26, final review F1 y F2).</summary>
public sealed record InboundForAutomation(
    Guid MessageId, MessageKind Kind, string? Text, string? ReplyId, InboundMediaForAutomation? Media, DateTimeOffset OccurredAt, string? Wamid = null,
    Guid? ReplyToMessageId = null, Guid? ConversationId = null);

/// <summary>Spec 2026-10-21 D-M1: la cola que la ingesta del webhook llena y el worker del motor vacía. El motor no corre dentro del
/// procesador del webhook porque éste no serializa por conversación y su reintento descarta el mismo wamid. Ojo: <c>message_id</c> no
/// siempre es un mensaje; la fila de un arranque manual (spec 2026-10-31) lleva un id propio sin fila en <c>messages</c>, y la de una
/// señal <c>ReleaseRun</c> (spec 2026-11-03) también. «Sin mensaje es manual» vale sólo dentro de <c>Engine</c>.</summary>
public interface IAutomationInbox
{
    // Complete y RetryLater toman la hora del reloj; Claim recibe `now` para que los leases y la regla «la más vieja de la conversación» se
    // comparen contra un solo instante, el que el worker usa en toda su pasada.
    /// <summary>Reclama hasta <paramref name="batch"/> filas vencidas con <c>SKIP LOCKED</c>, sólo la más vieja pendiente de cada
    /// conversación (el orden del cliente importa), y las deja en lease: 30 s el primer intento, 2 min el segundo y 10 min desde el tercero.
    /// Spec 2026-10-29 §6.1: sólo filas de <paramref name="botKind"/>; la regla de «la más vieja» mira las de todos los bots.</summary>
    Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, BotKind botKind, CancellationToken cancellationToken);

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

    /// <summary>Spec 2026-10-31: encola el arranque manual del asistente, pedido por una persona desde el hilo. Es una fila sin
    /// mensaje: su <c>message_id</c> es un id nuevo que no existe en <c>messages</c>, <c>opened</c> es <c>false</c> y el motor la reclama
    /// en el orden de su conversación, detrás de los entrantes anteriores. Sólo agrega al contexto: viaja en el <c>SaveChanges</c> del
    /// cambio que la pidió, para que el arranque y la fila queden juntos o no queden.</summary>
    void EnqueueManualStart(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now);

    /// <summary>Spec 2026-10-31: <c>true</c> si la conversación tiene una fila manual pendiente (<c>Engine</c>, sin cerrar y sin mensaje que
    /// la respalde). Los entrantes pendientes, las señales <c>ReleaseRun</c> y las filas ya cerradas no cuentan. Evita encolar un segundo arranque mientras el primero espera.</summary>
    Task<bool> HasPendingManualStartAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-03: encola la señal de soltar la conversación (una persona la tomó y el bot debe cerrar su corrida viva). Igual
    /// que <see cref="EnqueueManualStart"/>: fila sin mensaje, <c>opened = false</c>, sólo agrega al contexto y viaja en el
    /// <c>SaveChanges</c> del cambio que la pidió. Se reclama en el orden de su conversación, detrás de lo anterior.
    /// <paramref name="botKind"/> es el bot que tenía la conversación: la reclama su procesador (spec 2026-10-29 §6.1).</summary>
    void EnqueueReleaseRun(Guid tenantId, Guid connectionId, Guid conversationId, BotKind botKind, DateTimeOffset now);

    /// <summary>Spec 2026-11-17 §5.3: la fila <see cref="AutomationInboxKinds.Delegated"/> para el procesador de agentes. Fila sin mensaje,
    /// con la delegación en su columna; sólo agrega al contexto y viaja en el <c>SaveChanges</c> que crea la delegación y cambia la
    /// conversación. Una sola por delegación (<c>IX_automation_inbox_delegation</c>).</summary>
    void EnqueueDelegated(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now);

    /// <summary>Spec 2026-11-17 §6.3: la fila <see cref="AutomationInboxKinds.AgentReturned"/> para el motor de chatbots, en el
    /// <c>SaveChanges</c> que marca la delegación devuelta y le devuelve la conversación al chatbot. Una sola por delegación.</summary>
    void EnqueueAgentReturned(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now);

    /// <summary>Revisión de la rama, hallazgos 1 y 6: pasa a la cola de <paramref name="target"/> las filas <c>Engine</c> pendientes de la
    /// conversación que están en la del otro bot, menos <paramref name="exceptMessageId"/> (la que procesa quien llama). Las encoló la ingesta
    /// antes de que el cambio de bot se confirmara, así que el bot que ya no la tiene las perdía. Corre en el acto (SQL), así que va dentro de la
    /// transacción del cambio de bot; con la versión de la conversación cargada antes, un entrante que entre en el medio hace chocar el guardado.
    /// Devuelve la hora de la fila más vieja que movió, o <c>null</c> si no movió ninguna.</summary>
    Task<DateTimeOffset?> ReassignPendingEngineAsync(
        Guid tenantId, Guid conversationId, BotKind target, Guid? exceptMessageId, CancellationToken cancellationToken);
}
