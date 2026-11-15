using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-10-29 §6.2: lo que el agente necesita saber de la conversación de Mensajería (adaptador en Bootstrapper, como
/// <c>AutomationConversations</c>). Los estados viajan por nombre (<c>Open</c>, <c>Resolved</c>; <c>None</c>, <c>Active</c>, <c>Paused</c>;
/// <c>Chatbot</c>, <c>Agent</c>; <c>WhatsApp</c>, <c>Instagram</c>): Ai no referencia Messaging.
/// </summary>
/// <param name="WindowOpen">Si la ventana del canal sigue abierta ahora (<c>ChannelOutboundRules</c> de Mensajería).</param>
/// <param name="WindowRemaining">Lo que queda de esa ventana; <c>null</c> sin ventana.</param>
/// <param name="TextMaxLength">§6.5: el largo máximo de un texto en el canal.</param>
public sealed record AiConversationState(
    string Status,
    Guid? AssignedMemberId,
    string AutomationStatus,
    string? AutomationBotKind,
    Guid? AutomationAgentId,
    Guid ConnectionId,
    string Channel,
    Guid? CustomerId,
    DateTimeOffset? AutomationResumedAt,
    bool Blocked,
    bool WindowOpen,
    TimeSpan? WindowRemaining,
    int TextMaxLength)
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
    public const string None = "None";
    public const string Active = "Active";
    public const string Paused = "Paused";
    public const string AgentBot = "Agent";

    /// <summary>La conversación la atiende hoy <paramref name="agentId"/>, activo.</summary>
    public bool IsAttendedBy(Guid agentId) =>
        Status == Open && AssignedMemberId is null && AutomationStatus == Active && AutomationBotKind == AgentBot && AutomationAgentId == agentId;
}

/// <summary>Un mensaje del historial de la sesión leído de Mensajería (D-A28): el texto (o el pie) y qué es.</summary>
public sealed record AiThreadMessage(Guid Id, AgentTurnKind Kind, string? Text);

/// <summary>Spec 2026-10-29 §6.1 y §6.2: una fila <c>bot_kind = 2</c> de <c>messaging.automation_inbox</c>. <c>Attempts</c> es el testigo del
/// reclamo; <c>Opened</c> (D-M16) dice que el entrante abrió o reabrió la conversación.</summary>
public sealed record AiInboxItem(
    Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts, bool ReleaseRun);

/// <summary>La cola de entrantes de los agentes: la de Mensajería, filtrada por <c>bot_kind = 2</c> (adaptador en Bootstrapper).</summary>
public interface IAiInbox
{
    Task<IReadOnlyList<AiInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken);

    Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken);

    Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>Si la fila respalda un entrante de verdad (una fila manual o una señal no tiene mensaje).</summary>
    Task<bool> IsInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-29 §6.2, §6.5 y §6.6: lo que el agente le hace a la conversación. Cada cambio lo confirma Mensajería en el momento, con su
/// agregado y sus reglas; una regla que no se cumple sale como la excepción de dominio de siempre.
/// </summary>
public interface IAiConversations
{
    Task<AiConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>El agente toma la conversación (<c>StartAutomation(BotRef(Agent, id))</c>).</summary>
    Task StartAsync(Guid tenantId, Guid conversationId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>§6.6: pausa con causa y, si viene, asigna a <paramref name="assignToMemberId"/> (o sin asignar si ya no puede responder).</summary>
    Task HandoffAsync(Guid tenantId, Guid conversationId, AgentHandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken);

    /// <summary>D-A19: devuelve a <c>None</c> una conversación que el agente atiende; cualquier otra queda como está.</summary>
    Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>D-A28: los mensajes de los turnos, por id y del tenant; uno que ya no existe no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, AiThreadMessage>> ReadMessagesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);
}

public enum AiSendStatus
{
    Sent,
    NotAutomated,
    WindowClosed,
    Blocked,
    ConnectionUnavailable,
    Rejected,
}

/// <summary>Lo que respondió el envío del bot (<c>BotSendResult</c> de Mensajería). <see cref="MessageId"/> sólo con <see cref="AiSendStatus.Sent"/>.</summary>
public sealed record AiSendResult(AiSendStatus Status, Guid? MessageId = null, string? Code = null, string? ConnectionName = null);

/// <summary>Spec 2026-10-29 §6.5: el agente manda texto por <c>IBotOutbound</c>, con su <c>BotRef</c> y el <c>clientId</c> que lo hace
/// idempotente (D-A16).</summary>
public interface IAiBotOutbound
{
    Task<AiSendResult> SendTextAsync(
        Guid tenantId, Guid conversationId, Guid agentId, Guid clientId, string text, CancellationToken cancellationToken);
}

/// <summary>D-A9: el disparador <c>OutsideHours</c> pregunta el horario de la conexión (adaptador sobre <c>IBusinessHours</c>).</summary>
public interface IAiBusinessHours
{
    Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>§6.8: si el tenant tiene el módulo <c>ai</c>, con caché corta como <c>IAutomationsModuleStatus</c>.</summary>
public interface IAiModuleStatus
{
    Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una sesión viva candidata de un barrido (§6.8, D-A19).</summary>
public sealed record AgentSessionRef(Guid TenantId, Guid ConversationId);

/// <summary>Las sesiones y sus turnos (§4.2). Las lecturas <c>ForUpdate</c> sólo valen dentro de la transacción de
/// <see cref="IAiUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IAgentSessionRepository
{
    Task<AgentSession?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>La última sesión de la conversación, viva o no: el testigo de P2-R24 sobrevive al fin de la sesión.</summary>
    Task<AgentSession?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Sin seguimiento: es la lectura de la etapa larga, sin candado. Lo que se escribe se relee con
    /// <see cref="FindLiveForUpdateAsync"/> en el commit corto.</summary>
    Task<AgentSession?> FindAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);

    void Add(AgentSession session);

    void AddTurn(AgentTurn turn);

    /// <summary>Los turnos hasta <paramref name="upToSeq"/>, en orden.</summary>
    Task<IReadOnlyList<AgentTurn>> ListTurnsAsync(Guid sessionId, int upToSeq, CancellationToken cancellationToken);

    /// <summary>D-A14: si el cliente escribió después de <paramref name="seq"/>.</summary>
    Task<bool> HasCustomerTurnAfterAsync(Guid sessionId, int seq, CancellationToken cancellationToken);

    /// <summary>El trabajo <c>Pending</c> de la sesión (a lo sumo uno, <c>IX_model_jobs_session_open</c>).</summary>
    Task<ModelJob?> FindPendingJobAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>D-A19: sesiones vivas sin moverse desde <paramref name="cutoff"/>, de todos los tenants.</summary>
    Task<IReadOnlyList<AgentSessionRef>> ListIdleAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken);

    /// <summary>§6.8: las sesiones vivas de un tenant.</summary>
    Task<IReadOnlyList<AgentSessionRef>> ListLiveAsync(Guid tenantId, int batch, CancellationToken cancellationToken);

    /// <summary>§6.8: los tenants con alguna sesión viva.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithLiveSessionsAsync(CancellationToken cancellationToken);

    /// <summary>§4.7: borrar un agente exige que no tenga sesiones vivas.</summary>
    Task<bool> HasLiveSessionsAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>El agente <c>Active</c> que atiende la conexión, con sus versiones; <c>null</c> si ninguno.</summary>
    Task<Agent?> FindServingAgentAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>El agente con sus versiones, en cualquier estado (una sesión termina con su versión aunque el agente se pause).</summary>
    Task<Agent?> FindAgentAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-29 §6.2 paso 1: el candado de la conversación, con la transacción de Ai abierta. El mismo espacio y hash que el
/// del motor de chatbots, para que los dos procesadores nunca trabajen la misma conversación a la vez.</summary>
public interface IAiSessionUnitOfWork
{
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary><c>pg_try_advisory_xact_lock</c>: sin esperar (P2-R45).</summary>
    Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Guarda y, si la unidad abrió la transacción, la confirma y suelta el candado.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Lo que el procesador de entrantes escribe en <c>automation_inbox.outcome</c> (≤ 24).</summary>
public static class AiInboxOutcomes
{
    public const string Started = "started";
    public const string Continued = "continued";
    public const string Skipped = "skipped";
    public const string Duplicate = "duplicate";
    public const string Interrupted = "interrupted";
    public const string ModuleDisabled = "module_disabled";
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>Revisión final, hallazgo 4: la conversación era del agente, que no la puede retomar; pasó a una persona.</summary>
    public const string HandedOff = "handed_off";
    public const string Retry = "retry";
    public const string Locked = "locked";
}

/// <summary>Los motivos de fin de una sesión (<c>ai.sessions.end_reason</c>, ≤ 32).</summary>
public static class AgentSessionEndReasons
{
    public const string HumanTookOver = "human_took_over";
    public const string ModuleDisabled = "module_disabled";
    public const string Abandoned = "abandoned";
    public const string Blocked = "blocked";
    public const string WindowClosed = "window_closed";
}

/// <summary>D-A16: ids que se repiten en un reintento, para que Mensajería (idempotente por <c>clientId</c>) no duplique y una sesión no
/// nazca dos veces del mismo entrante.</summary>
public static class AiDeterministicIds
{
    public static Guid SessionFor(Guid inboundMessageId) => From($"ai-session:{inboundMessageId:D}");

    public static Guid GreetingClientId(Guid sessionId) => From($"session:{sessionId:D}:greeting");

    public static Guid ReplyClientId(Guid jobId, int part) => From($"job:{jobId:D}:{part}");

    public static Guid HandoffClientId(Guid jobId) => From($"job:{jobId:D}:handoff");

    private static Guid From(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
