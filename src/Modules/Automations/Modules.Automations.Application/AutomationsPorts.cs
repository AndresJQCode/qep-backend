using Modules.Automations.Domain;

namespace Modules.Automations.Application;

/// <summary><c>Versions</c> y <c>Connections</c> viajan siempre con el chatbot: el agregado numera las versiones con
/// <c>Versions.Count</c> y <c>HasDraftChanges</c> las lee. Todo método recibe el tenant (doble aislamiento).</summary>
public interface IChatbotRepository
{
    Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Ordenados por nombre; <paramref name="search"/> filtra por nombre sin mayúsculas.</summary>
    Task<IReadOnlyList<Chatbot>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken);

    /// <summary>El chatbot publicado que reserva la conexión (fila activa de <c>chatbot_connections</c>); un borrador no cuenta. Puede
    /// venir <c>Active</c> o <c>Paused</c> (D-C13: uno en pausa conserva la conexión reservada pero no contesta): quien llama revisa
    /// <c>Status</c> antes de responder.</summary>
    Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken);

    void Remove(Chatbot chatbot);

    /// <summary>Misma regla que el índice único: <c>lower(name)</c> dentro del tenant.</summary>
    Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken);
}

public interface IChatbotTemplateRepository
{
    Task<ChatbotTemplate?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatbotTemplate>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAsync(ChatbotTemplate chatbotTemplate, CancellationToken cancellationToken);

    void Remove(ChatbotTemplate chatbotTemplate);
}

/// <summary>Lecturas sobre las corridas del motor (el registro es interno de Infrastructure).</summary>
public interface IConversationRunQueries
{
    /// <summary>Corridas con <c>started_at</c> en la ventana, por chatbot: <c>Handled</c> todas, <c>HandedOff</c> las terminadas así.
    /// Un chatbot sin corridas no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
        Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken);

    /// <summary>¿Hay corridas <c>Running</c> o <c>Waiting</c>? Bloquea el borrado.</summary>
    Task<bool> HasLiveRunsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken);
}

/// <summary>Traduce por nombre de índice (nunca sólo por SqlState): nombre repetido y conexión ocupada → 409; versión vieja o
/// dos publicaciones simultáneas → <c>concurrency.conflict</c> (412).</summary>
public interface IAutomationsUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-21 §4.6: auditoría atómica propia, en la misma transacción que el cambio. Lo del motor va con
/// <paramref name="bySystem"/>.</summary>
public interface IAutomationsAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at);
}

public static class AutomationsAuditActions
{
    public const string ResourceChatbot = "chatbot";
    public const string ResourceTemplate = "chatbot_template";

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
}
