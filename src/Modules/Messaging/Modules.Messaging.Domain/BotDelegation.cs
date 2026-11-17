namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: se guarda por nombre (<c>CK_bot_delegations_status</c>); no se renombra.</summary>
public enum BotDelegationStatus
{
    /// <summary>El agente atiende la conversación por el chatbot.</summary>
    Live,

    /// <summary>El agente devolvió la conversación al flujo con su resultado.</summary>
    Returned,

    /// <summary>Una persona tomó la conversación (o la delegación quedó huérfana): nadie vuelve al flujo.</summary>
    Interrupted,
}

/// <summary>
/// Spec 2026-11-17 §5.3 (D-B3): el chatbot le pasa una conversación a un agente de IA y la recupera. Vive en Mensajería porque es la única
/// pieza que Automations y Ai ven (D-A3): chatbot, corrida, bloque y agente son referencias blandas. <see cref="Brief"/> lo arma Automations
/// y lo lee Ai, y <see cref="Result"/> lo escribe Ai y lo lee Automations; Mensajería no interpreta ninguno de los dos JSON.
/// <para>Nunca hay dos vivas de una conversación (<c>IX_bot_delegations_live</c>), y una viva sólo existe mientras la conversación la
/// apunta (<c>conversations.automation_delegation_id</c>).</para>
/// </summary>
public sealed class BotDelegation
{
    /// <summary>El id de un bloque del flujo mide como los de <c>AutomationHandoff</c>.</summary>
    public const int NodeIdMaxLength = 64;

    private BotDelegation() { }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid ChatbotId { get; private set; }

    public Guid RunId { get; private set; }

    public string NodeId { get; private set; } = string.Empty;

    public Guid AgentId { get; private set; }

    /// <summary>D-B7: el último entrante que llevó la corrida hasta el bloque; <c>null</c> si llegó sin entrante (arranque manual).</summary>
    public Guid? LastInboundMessageId { get; private set; }

    /// <summary>JSON <c>{objective, context, firstTurn, idleMinutes}</c> con el contexto ya interpolado (§8: puede traer datos del cliente).</summary>
    public string Brief { get; private set; } = string.Empty;

    public BotDelegationStatus Status { get; private set; }

    /// <summary>JSON <c>{output, reason, summary?, quotationNumber?, advisorMemberId?}</c>; sólo con <see cref="BotDelegationStatus.Returned"/>.</summary>
    public string? Result { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public static BotDelegation Start(
        Guid id, Guid tenantId, Guid conversationId, Guid connectionId, Guid chatbotId, Guid runId, string nodeId, Guid agentId,
        Guid? lastInboundMessageId, string brief, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeId.Length, NodeIdMaxLength, nameof(nodeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        return new BotDelegation
        {
            Id = id,
            TenantId = tenantId,
            ConversationId = conversationId,
            ConnectionId = connectionId,
            ChatbotId = chatbotId,
            RunId = runId,
            NodeId = nodeId,
            AgentId = agentId,
            LastInboundMessageId = lastInboundMessageId,
            Brief = brief,
            Status = BotDelegationStatus.Live,
            StartedAt = now,
        };
    }

    /// <summary>§6.3: el agente devuelve con su resultado. Sólo desde <c>Live</c>: una devuelta o interrumpida ya no tiene a quién volver.</summary>
    public void Return(string resultJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        if (Status != BotDelegationStatus.Live)
        {
            throw new MessagingDomainException(MessagingErrorCodes.DelegationNotLive, "Only a live delegation can return to the chatbot.");
        }

        Status = BotDelegationStatus.Returned;
        Result = resultJson;
        EndedAt = now;
    }

    /// <summary>§6.4 y §6.5: una persona tomó la conversación, o la delegación quedó huérfana. <c>false</c> si ya había terminado: una señal
    /// tardía no pisa un retorno que ya ocurrió.</summary>
    public bool Interrupt(DateTimeOffset now)
    {
        if (Status != BotDelegationStatus.Live)
        {
            return false;
        }

        Status = BotDelegationStatus.Interrupted;
        EndedAt = now;
        return true;
    }
}
