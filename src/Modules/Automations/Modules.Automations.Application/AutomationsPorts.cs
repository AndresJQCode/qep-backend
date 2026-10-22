using Modules.Automations.Domain;

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

/// <summary>La conexión <see cref="ConnectionId"/> la atiende el chatbot publicado <see cref="ChatbotId"/>.</summary>
public sealed record ChatbotServingConnection(Guid ConnectionId, Guid ChatbotId, string Name);

public interface IChatbotTemplateRepository
{
    Task<ChatbotTemplate?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatbotTemplate>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAsync(ChatbotTemplate chatbotTemplate, CancellationToken cancellationToken);

    void Remove(ChatbotTemplate chatbotTemplate);
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
}

/// <summary>El horario asignado a cada conexión (Mensajería, D-H10). Una conexión sin horario viene con <c>null</c> o no viene.</summary>
public interface IAutomationBusinessHours
{
    Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken);
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

    /// <summary>D-M12: <c>pg_advisory_xact_lock</c> sobre la conversación, también cuando aún no tiene corrida que bloquear (dos
    /// entrantes que la inician a la vez). Exige la transacción abierta. Se toma primero y después se lee con
    /// <see cref="IConversationRunRepository.FindLiveForUpdateAsync"/> (P2-R20).</summary>
    Task LockConversationAsync(Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §5.2 y D-M12/D-M13: las corridas del motor. Las lecturas <c>ForUpdate</c> y el barrido sólo valen dentro de
/// la transacción de <see cref="IAutomationsUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IConversationRunRepository
{
    /// <summary>La corrida viva de la conversación, bloqueada con <c>SELECT … FOR UPDATE</c>. Llamar antes a
    /// <see cref="IAutomationsUnitOfWork.LockConversationAsync"/> (P2-R20). Exige la transacción abierta.</summary>
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
