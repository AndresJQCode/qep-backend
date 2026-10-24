using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application;

/// <summary><c>Versions</c> y <c>Connections</c> viajan siempre con el chatbot: el agregado numera las versiones con
/// <c>Versions.Count</c> y <c>HasDraftChanges</c> las lee. Todo método recibe el tenant (doble aislamiento).</summary>
public interface IChatbotRepository
{
    Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>El chatbot publicado que reserva la conexión (fila activa de <c>chatbot_connections</c>); un borrador no cuenta. Puede
    /// venir <c>Active</c> o <c>Paused</c> (D-C13: uno en pausa conserva la conexión reservada pero no contesta): quien llama revisa
    /// <c>Status</c> antes de responder.</summary>
    Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Ruling P2-R6: lo que el motor lee por cada entrante sin corrida, proyectado y sin seguimiento (el agregado trae el flujo
    /// de todas las versiones). El publicado que reserva la conexión, activo o en pausa: quien llama revisa <c>Status</c>.</summary>
    Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>El <c>flow</c> de una sola versión publicada; <c>null</c> si no existe en el tenant (el chatbot se borró).</summary>
    Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>Palabra de reinicio e intentos del chatbot (D-C7) para una corrida viva, que sigue aunque el chatbot ya no atienda la
    /// conexión o esté en pausa (D-C1, D-C5); <c>null</c> si el chatbot ya no existe.</summary>
    Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken);

    /// <summary>Lo que la lista pinta, proyectado y sin seguimiento: sin los jsonb del borrador ni de las versiones. Ordenados por
    /// nombre (y por id para desempatar); <paramref name="search"/> filtra por nombre sin mayúsculas, con los comodines como literales.</summary>
    Task<IReadOnlyList<ChatbotSummaryRow>> ListSummariesAsync(Guid tenantId, string? search, CancellationToken cancellationToken);

    /// <summary>Qué chatbot publicado (activo o en pausa, D-C13) ocupa cada conexión: las filas activas de <c>chatbot_connections</c>.
    /// Proyectado y sin seguimiento, para el <c>servedBy</c> del editor.</summary>
    Task<IReadOnlyList<ChatbotServingConnection>> ListServingConnectionsAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken);

    void Remove(Chatbot chatbot);

    /// <summary>Misma regla que el índice único: <c>lower(name)</c> dentro del tenant.</summary>
    Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken);
}

/// <summary>Una fila de la lista de chatbots (§4.4). <see cref="PublishedNumber"/> y <see cref="PublishedAt"/> son los de la versión
/// publicada, <c>null</c> mientras es borrador.</summary>
public sealed record ChatbotSummaryRow(
    Guid Id,
    string Name,
    string Description,
    ChatbotStatus Status,
    ChatbotTrigger Trigger,
    IReadOnlyList<Guid> ConnectionIds,
    int? PublishedNumber,
    DateTimeOffset? PublishedAt,
    DateTimeOffset DraftUpdatedAt,
    long Version)
{
    public bool HasDraftChanges => Chatbot.DraftChangedSince(DraftUpdatedAt, PublishedAt);
}

/// <summary>Ruling P2-R6: el chatbot publicado que atiende una conexión, sin borrador ni versiones. <see cref="Keywords"/> vienen
/// normalizadas por el agregado.</summary>
public sealed record ServingChatbot(
    Guid ChatbotId, string Name, ChatbotStatus Status, ChatbotTrigger Trigger, IReadOnlyList<string> Keywords, string RestartKeyword,
    int MaxAttempts, Guid PublishedVersionId);

/// <summary>La conexión <see cref="ConnectionId"/> la atiende el chatbot publicado <see cref="ChatbotId"/>.</summary>
public sealed record ChatbotServingConnection(Guid ConnectionId, Guid ChatbotId, string Name);

public interface IChatbotTemplateRepository
{
    Task<ChatbotTemplate?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatbotTemplate>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAsync(ChatbotTemplate chatbotTemplate, CancellationToken cancellationToken);

    void Remove(ChatbotTemplate chatbotTemplate);
}

/// <summary>Spec 2026-10-21 §8 y D-S1: las sesiones del simulador. Todo método recibe el tenant, salvo el barrido, que es del sistema.</summary>
public interface ISimulationRepository
{
    Task<Simulation?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task AddAsync(Simulation simulation, CancellationToken cancellationToken);

    void Remove(Simulation simulation);

    /// <summary>D-S1: borra las vencidas (<c>expires_at &lt;= now</c>) de todos los tenants y devuelve cuántas.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Lecturas sobre las corridas del motor (el esquema es de Infrastructure).</summary>
public interface IConversationRunQueries
{
    /// <summary>Corridas con <c>started_at</c> en la ventana, por chatbot: <c>Handled</c> todas, <c>HandedOff</c> las terminadas así.
    /// Un chatbot sin corridas no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
        Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken);

    /// <summary>¿Hay corridas <c>Running</c> o <c>Waiting</c>? Bloquea el borrado.</summary>
    Task<bool> HasLiveRunsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §4.4 y D-M6: una conexión de Mensajería del tenant, en cualquier estado. <see cref="Channel"/> es el nombre
/// de <c>ConversationChannel</c> (<c>WhatsApp</c> o <c>Instagram</c>); <see cref="Handle"/>, el número visible o el <c>@usuario</c>, vacío
/// si la conexión no está activa; <see cref="Status"/>, el nombre del estado de Integrations (<c>Active</c>, <c>Paused</c>, …).</summary>
public sealed record AutomationConnectionRef(Guid Id, string Name, string Channel, string Handle, string Status);

/// <summary>Las conexiones de Mensajería del tenant (adaptador en Bootstrapper sobre <c>IMessagingConnectionDirectory</c>): Automations
/// no referencia Messaging.</summary>
public interface IAutomationConnections
{
    Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una conexión «API externa» del tenant (D-H4); <see cref="Status"/> es el nombre del estado de Integrations.</summary>
public sealed record ExternalApiRef(Guid Id, string Name, string Status);

/// <summary>Las conexiones «API externa» del tenant, que el bloque HTTP usa (adaptador en Bootstrapper sobre Integrations).</summary>
public interface IAutomationExternalApis
{
    Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>D-H5: la URL base, el header y la clave de la conexión, al momento de llamar. Sin credenciales si no es del tenant, no es
    /// <c>external-api</c>, no está <c>Active</c> o su clave no se puede leer; entonces trae el motivo (spec 2026-10-24 D-T5).</summary>
    Task<ExternalApiResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-24 §3.2: el resultado de resolver una conexión «API externa». Las credenciales nunca viajan con un motivo.</summary>
/// <param name="UnavailableReason"><c>null</c> si <paramref name="Credentials"/> no es <c>null</c>; si no, un código <c>snake_case</c> de
/// spec §3.4 (<c>not_found</c>, <c>paused</c>, <c>secret_unreadable</c>, <c>wrong_provider</c>, <c>incomplete</c>, …).</param>
/// <param name="ConnectionName">El nombre de la conexión cuando existe (para el hilo); <c>null</c> si no existe.</param>
public sealed record ExternalApiResolution(ExternalApiCredentials? Credentials, string? UnavailableReason, string? ConnectionName);

/// <summary>D-H5: lo que el bloque HTTP necesita para llamar. La clave vive en memoria lo que dura la llamada: nunca se interpola, ni se
/// guarda en la corrida, ni se registra, y <see cref="ToString"/> no la imprime (como <c>ResolvedConnection</c> de Integrations).</summary>
public sealed record ExternalApiCredentials(Uri BaseUrl, string AuthHeader, string ApiKey)
{
    public override string ToString() => $"ExternalApiCredentials {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>
/// Spec 2026-10-21 D-H2 y fallos P2-R30/P2-R31: la política anti-SSRF de Integrations (adaptador en Bootstrapper sobre
/// <c>ExternalApiUrlPolicy</c>): Automations no referencia Integrations. El handler que juzga cada conexión lo pone el registro del cliente
/// nombrado, también en Bootstrapper.
/// </summary>
public interface IExternalApiUrlPolicy
{
    /// <summary><c>null</c> si la URL base se acepta; si no, el motivo técnico (<c>scheme_not_https</c>, <c>host_is_ip_literal</c>, …).</summary>
    string? Reject(Uri url);

    /// <summary>P2-R30: el header de la clave no puede ser uno del transporte (<c>Host</c>, <c>Content-*</c>, …).</summary>
    bool IsReservedHeader(string name);

    /// <summary>El motivo si la excepción de <c>HttpClient</c> es el rechazo al conectar (el host resolvió a un rango interno); <c>null</c>
    /// si es otra falla de red.</summary>
    string? RejectionIn(Exception exception);
}

/// <summary>El horario asignado a cada conexión (Mensajería, D-H10). Una conexión sin horario viene con <c>null</c> o no viene.</summary>
public interface IAutomationBusinessHours
{
    Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken);

    /// <summary>El bloque Horario (§5.4): ¿la conexión está en horario ahora? Una conexión sin horario cuenta como abierta, como en
    /// <c>IBusinessHours.GetStatusAsync</c> de Mensajería.</summary>
    Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §5.1: una fila reclamada de <c>messaging.automation_inbox</c>. <see cref="Opened"/> (D-M16) dice que el
/// entrante abrió o reabrió la conversación; <see cref="Attempts"/> ya cuenta el reclamo que la devolvió y es el testigo con que se cierra.</summary>
public sealed record InboundQueueItem(Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts);

/// <summary><see cref="Stored"/>: la copia a R2 ya terminó; mientras sea <c>false</c> el adjunto no se puede servir ni reenviar.</summary>
public sealed record InboundAttachment(string MimeType, string? FileName, bool Stored);

/// <summary>Lo que el motor lee de un entrante. <see cref="Kind"/> es el nombre del <c>MessageKind</c> de Mensajería
/// (<c>CustomerMessageKinds</c>); <see cref="Text"/> es el texto o, si no tiene, la leyenda del adjunto (P2-R4: una palabra clave
/// también se dispara con la leyenda de una foto).</summary>
public sealed record InboundMessage(Guid MessageId, string Kind, string? Text, string? ReplyId, InboundAttachment? Attachment, DateTimeOffset OccurredAt);

/// <summary>Spec 2026-10-21 §5.1 y §5.3: la cola del motor (adaptador en Bootstrapper sobre <c>IAutomationInbox</c> de Mensajería).
/// <c>Complete</c> y <c>RetryLater</c> llevan el <c>Attempts</c> del reclamo como testigo (P2-R3): <c>false</c> = otra réplica volvió a
/// reclamar la fila después de vencido el lease, y lo que hizo este worker ya no cuenta.</summary>
// CA1711: el analizador asume que «Queue» implica heredar de System.Collections.Queue; acá es el sustantivo del spec (§5.3), como
// IExportJobQueue de Quotations: falso positivo.
#pragma warning disable CA1711
public interface IAutomationInboundQueue
#pragma warning restore CA1711
{
    Task<IReadOnlyList<InboundQueueItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken);

    Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken);

    /// <summary>Con 5 intentos ya hechos, Mensajería la cierra con <c>failed</c> en vez de reprogramarla.</summary>
    Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary><c>null</c> si el mensaje no es un entrante de ese tenant.</summary>
    Task<InboundMessage?> ReadAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Ruling P2-R7: borra hasta <paramref name="batch"/> filas ya procesadas antes de <paramref name="cutoff"/>, de todos los
    /// tenants, y devuelve cuántas; las pendientes quedan.</summary>
    Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken);
}

/// <summary>Lo que el motor necesita de la conversación (§5.2 paso 2). <see cref="Status"/> es <c>Open</c> o <c>Resolved</c>;
/// <see cref="AutomationStatus"/>, <c>None</c>, <c>Active</c> o <c>Paused</c> (los nombres de los enums de Mensajería).
/// <see cref="CustomerName"/> es el nombre de perfil de WhatsApp y <see cref="Phone"/>, el <c>wa_id</c>.
/// <see cref="AutomationResumedAt"/> (D-M15, P2-R25) es cuándo una persona la devolvió al asistente por última vez.</summary>
public sealed record AutomationConversationState(
    string Status, Guid? AssignedMemberId, string AutomationStatus, Guid? AutomationChatbotId, Guid ConnectionId,
    DateTimeOffset? LastInboundAt, string? CustomerName, string? Phone, DateTimeOffset? AutomationResumedAt = null);

/// <summary>Spec 2026-10-21 §5.3, D-M5 y D-M14: lo que el motor le hace a la conversación (adaptador en Bootstrapper sobre el agregado
/// <c>Conversation</c> de Mensajería). Cada cambio se guarda y se notifica antes de volver. Una regla del agregado que no se cumple
/// (con asesor, ya resuelta, ya pausada) sale como el <c>DomainException</c> de Mensajería, y una versión vieja como
/// <c>RequestConcurrencyException</c> (<c>concurrency.conflict</c>).</summary>
public interface IAutomationConversations
{
    /// <summary><c>null</c> si la conversación no es del tenant.</summary>
    Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary><c>None → Active</c> con el chatbot y el primer paso.</summary>
    Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken);

    /// <summary>El paso visible en la bandeja (D-M14). No es un cambio de la conversación: no mueve su versión ni falla porque
    /// entró un mensaje; sin asistente no escribe nada.</summary>
    Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken);

    /// <summary>Ruling P2-R26: el chatbot que atiende, cuando una corrida arranca sobre una conversación devuelta al asistente y la conexión
    /// ya la sirve otro (el chip de la bandeja muestra el que corre). Como <see cref="SetStepAsync"/>, no es un cambio de la conversación:
    /// no mueve su versión; sin asistente activo no escribe nada.</summary>
    Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken);

    /// <summary>D-M5: <c>Paused</c> con motivo <c>Handoff</c>, sin asesor, con evento en el hilo, auditoría de sistema y aviso a todo el
    /// tenant como un entrante sin asignar.</summary>
    Task HandoffAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>D-M5: resuelta con el motivo (si sigue activo en el tenant), con evento <c>Resolved</c> sin actor y auditoría de sistema.</summary>
    Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken cancellationToken);

    /// <summary>D-M14: <c>Active → None</c>, sin evento. Si el asistente ya no está activo (terminó, lo pausó una persona o la
    /// conversación se resolvió) no hace nada.</summary>
    Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>Cómo terminó un envío del bot (D-I4). <see cref="NotAutomated"/>: la conversación ya no es del bot (resuelta, con asesor o
/// sin asistente activo). <see cref="Rejected"/> lleva el código de Mensajería y, cuando la conexión de WhatsApp no se pudo usar, su motivo
/// y su nombre (spec 2026-10-24 §3.3).</summary>
public abstract record BotSendOutcome
{
    public sealed record Sent(Guid MessageId) : BotSendOutcome;

    public sealed record NotAutomated : BotSendOutcome;

    public sealed record WindowClosed : BotSendOutcome;

    public sealed record Rejected(string Code, string? ConnectionReason = null, string? ConnectionName = null) : BotSendOutcome;
}

/// <summary>Spec 2026-10-21 §5.3 y D-I4: los envíos del bot (adaptador en Bootstrapper sobre <c>IBotOutbound</c>). El
/// <paramref name="clientId"/> lo deriva el procesador (P2-R5): un reintento con el mismo id no vuelve a mandar.</summary>
public interface IAutomationMessenger
{
    Task<BotSendOutcome> SendTextAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, string text, CancellationToken cancellationToken);

    Task<BotSendOutcome> SendInteractiveAsync(
        Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, InteractiveMessage message, CancellationToken cancellationToken);
}

/// <summary>El adjunto de un entrante ya copiado al almacén: <see cref="Key"/> es la llave en R2.</summary>
public sealed record StoredAttachment(string Key, string? FileName, string MimeType, long? SizeBytes);

/// <summary>Spec 2026-10-21 §5.3 y D-M8: los adjuntos que el bloque Adjunto guarda y el bloque HTTP reenvía (adaptador en Bootstrapper
/// sobre <c>IMediaReads</c> e <c>IMessagingMediaStore</c>).</summary>
public interface IAutomationMedia
{
    /// <summary><c>null</c> si el mensaje no es del tenant, no tiene adjunto o todavía no se copió.</summary>
    Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary><c>null</c> si el objeto no está. El stream es de quien llama.</summary>
    Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>El nombre de la empresa para <c>{empresa}</c> (adaptador en Bootstrapper sobre Tenancy).</summary>
public interface IAutomationTenantNames
{
    Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Traduce por nombre de índice (nunca sólo por SqlState): nombre repetido y conexión ocupada → 409; versión vieja o
/// dos publicaciones simultáneas → <c>concurrency.conflict</c> (412).</summary>
public interface IAutomationsUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Abre la transacción de una unidad del motor. <see cref="SaveChangesAsync"/> la confirma y da la unidad por terminada:
    /// un segundo guardado en el mismo scope corre sin la transacción ni los locks, así que el procesador guarda una sola vez por
    /// unidad. Disponer el handle sin haber guardado la deshace. Los <c>FOR UPDATE</c> y el lock por conversación sólo valen dentro
    /// de ella.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// D-M12: <c>pg_try_advisory_xact_lock</c> sobre la conversación, también cuando aún no tiene corrida que bloquear (dos entrantes que
    /// la inician a la vez). Exige la transacción abierta. Se toma primero y después se lee con
    /// <see cref="IConversationRunRepository.FindLiveForUpdateAsync"/> (P2-R20). Ruling P2-R45: no espera; <c>false</c> = otra unidad lo
    /// tiene (la misma fila, reclamada otra vez porque la unidad original pasó su lease), y quien llama reprograma en vez de quedarse
    /// bloqueado detrás de ella.
    /// </summary>
    Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>
/// Ruling P2-R42: ¿el tenant tiene el módulo <c>automations</c> activo? La ingesta de Mensajería sólo mira <c>messaging</c>, así que con
/// <c>automations</c> apagado las filas del inbox siguen llegando y el motor es quien deja de contestar. Cacheado por tenant como
/// <c>WebhookRouting.IsModuleEnabledAsync</c> de Mensajería: apagar tarda hasta un minuto en verse (aceptado, como D-M18).
/// </summary>
public interface IAutomationsModuleStatus
{
    Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §5.2 y D-M12/D-M13: las corridas del motor. Las lecturas <c>ForUpdate</c> y el barrido sólo valen dentro de
/// la transacción de <see cref="IAutomationsUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IConversationRunRepository
{
    /// <summary>La corrida viva de la conversación, bloqueada con <c>SELECT … FOR UPDATE</c>. Llamar antes a
    /// <see cref="IAutomationsUnitOfWork.TryLockConversationAsync"/> (P2-R20). Exige la transacción abierta.</summary>
    Task<ConversationRun?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    Task<ConversationRun?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    Task AddAsync(ConversationRun run, CancellationToken cancellationToken);

    /// <summary>Las corridas del chatbot iniciadas en [<paramref name="from"/>, <paramref name="until"/>), de la más nueva a la más
    /// vieja, hasta 500.</summary>
    Task<IReadOnlyList<ConversationRun>> ListAsync(
        Guid tenantId, Guid chatbotId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>D-M13: las <c>Waiting</c> con <c>updated_at</c> anterior a <paramref name="waitingBefore"/>, tomadas con
    /// <c>FOR UPDATE SKIP LOCKED</c> para que dos barridos no se pisen. De todos los tenants: es un barrido del sistema.</summary>
    Task<IReadOnlyList<ConversationRun>> ClaimAbandonedAsync(DateTimeOffset waitingBefore, int limit, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §4.6: auditoría atómica propia, en la misma transacción que el cambio. Lo del motor va con
/// <paramref name="bySystem"/>.</summary>
public interface IAutomationsAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at);
}

/// <summary>Lecturas sobre <c>audit.entries</c> que el módulo necesita para no auditar de más (el registro es interno de
/// Infrastructure).</summary>
public interface IAutomationsAuditQueries
{
    /// <summary>¿Se auditó un guardado de borrador (<c>draft_saved</c>) de este chatbot en <paramref name="since"/> o después? Solo cuentan
    /// las entradas <c>draft_saved</c>: crear, editar o publicar no frenan la primera.</summary>
    Task<bool> HasDraftSavedSinceAsync(Guid tenantId, Guid chatbotId, DateTimeOffset since, CancellationToken cancellationToken);
}

public static class AutomationsAuditActions
{
    public const string ResourceChatbot = "chatbot";
    public const string ResourceTemplate = "chatbot_template";
    public const string ResourceRun = "conversation_run";

    public const string ChatbotCreated = "automations.chatbot.created";
    public const string ChatbotUpdated = "automations.chatbot.updated";
    public const string ChatbotDraftSaved = "automations.chatbot.draft_saved";
    public const string ChatbotPublished = "automations.chatbot.published";
    /// <summary>Una conexión pasó de un chatbot publicado a otro (spec 2026-10-23 D-X6).</summary>
    public const string ChatbotConnectionReleased = "automations.chatbot.connection_released";
    public const string ChatbotPaused = "automations.chatbot.paused";
    public const string ChatbotResumed = "automations.chatbot.resumed";
    public const string ChatbotDeleted = "automations.chatbot.deleted";
    public const string ChatbotImported = "automations.chatbot.imported";
    public const string TemplateCreated = "automations.template.created";
    public const string TemplateDeleted = "automations.template.deleted";
    public const string RunStarted = "automations.run.started";
    public const string RunHandedOff = "automations.run.handed_off";
    public const string RunResolved = "automations.run.resolved";
    public const string RunCompleted = "automations.run.completed";
    public const string RunFailed = "automations.run.failed";

    /// <summary>No está en la lista de §4.6: el barrido de D-M13 termina corridas sin una persona y la auditoría es la única huella.</summary>
    public const string RunAbandoned = "automations.run.abandoned";
}
