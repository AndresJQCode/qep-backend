namespace Modules.Ai.Domain;

/// <summary>Spec 2026-10-29 §4.2: cómo está o cómo terminó la atención de un agente en una conversación. Se guarda por nombre.</summary>
public enum AgentSessionStatus
{
    Live,
    HandedOff,
    WindowClosed,
    Interrupted,
    Failed,
    Abandoned,
}

/// <summary>
/// Spec 2026-10-29 §4.2 y §4.7: la atención de un agente en una conversación, de su primer entrante al traspaso, la toma de una persona o el
/// abandono. Termina con la versión con que empezó (D-A5) y, con ella, con su proveedor (plan 2026-11-12 C-17). Una sola viva por
/// conversación (<c>IX_sessions_live</c>). Sin token de concurrencia: sólo se escribe con el candado de la conversación tomado.
/// </summary>
public sealed class AgentSession
{
    /// <summary>D-A14: los descartes seguidos que se toleran; al tercero se envía igual.</summary>
    public const int MaxSupersededInRow = 2;

    public const int ReasonMaxLength = 32;

    public const int FailureCodeMaxLength = 64;

    private AgentSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid AgentId { get; private set; }

    public Guid AgentVersionId { get; private set; }

    public AgentSessionStatus Status { get; private set; }

    /// <summary>El número del próximo turno (1, 2, 3…).</summary>
    public int NextSeq { get; private set; }

    public int RepliesWithoutHuman { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public int SupersededInRow { get; private set; }

    /// <summary>P2-R24: el último entrante que esta sesión ya procesó; un reintento de la misma fila de la cola es <c>duplicate</c>.</summary>
    public Guid? LastInboundMessageId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Lo mueve cada turno: el barrido de D-A19 mira cuánto lleva sin moverse.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndReason { get; private set; }

    public string? FailureCode { get; private set; }

    public bool IsLive => Status == AgentSessionStatus.Live;

    public static AgentSession Open(
        Guid id, Guid tenantId, Guid conversationId, Guid connectionId, Guid agentId, Guid agentVersionId, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            ConversationId = conversationId,
            ConnectionId = connectionId,
            AgentId = agentId,
            AgentVersionId = agentVersionId,
            Status = AgentSessionStatus.Live,
            NextSeq = 1,
            StartedAt = now,
            UpdatedAt = now,
        };

    /// <summary>Anota un turno y devuelve su número.</summary>
    public AgentTurn AppendTurn(AgentTurnRoleKind role, Guid? messageId, Guid? jobId, IReadOnlyList<string> sources, bool truncated, DateTimeOffset now)
    {
        EnsureLive();
        var turn = AgentTurn.Create(TenantId, Id, NextSeq, role, messageId, jobId, sources, truncated, now);
        NextSeq++;
        UpdatedAt = now;
        return turn;
    }

    public void MarkInbound(Guid messageId) => LastInboundMessageId = messageId;

    /// <summary>Una respuesta salió: cuenta para D-A21 y limpia los fallos y los descartes seguidos.</summary>
    public void RecordReply(DateTimeOffset now)
    {
        EnsureLive();
        RepliesWithoutHuman++;
        ConsecutiveFailures = 0;
        SupersededInRow = 0;
        UpdatedAt = now;
    }

    /// <summary>D-A21: un trabajo que falló sin traspaso inmediato. Devuelve los fallos seguidos.</summary>
    public int RecordFailure(DateTimeOffset now)
    {
        EnsureLive();
        ConsecutiveFailures++;
        UpdatedAt = now;
        return ConsecutiveFailures;
    }

    /// <summary>D-A14: el trabajo se descartó porque el cliente siguió escribiendo.</summary>
    public void RecordSuperseded(DateTimeOffset now)
    {
        EnsureLive();
        SupersededInRow++;
        UpdatedAt = now;
    }

    /// <summary>D-A14: si la respuesta de un trabajo todavía se puede descartar por turnos más nuevos.</summary>
    public bool CanSupersede => SupersededInRow < MaxSupersededInRow;

    public void Finish(AgentSessionStatus status, string? reason, string? failureCode, DateTimeOffset now)
    {
        if (status == AgentSessionStatus.Live)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A session finishes in a final status.");
        }

        EnsureLive();
        Status = status;
        EndReason = Fit(reason, ReasonMaxLength);
        FailureCode = Fit(failureCode, FailureCodeMaxLength);
        EndedAt = now;
        UpdatedAt = now;
    }

    private void EnsureLive()
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("The session already ended.");
        }
    }

    private static string? Fit(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Spec 2026-10-29 §4.2: quién habló en un turno. Se guarda por nombre.</summary>
public enum AgentTurnRoleKind
{
    Customer,
    Agent,
    Greeting,
}

/// <summary>
/// Spec 2026-10-29 §4.2 y D-A28: un turno de la sesión. No copia el texto: guarda el <c>message_id</c> de Mensajería y el prompt se arma
/// leyéndolo por puerto. <see cref="Truncated"/> (§6.3): la respuesta pasaba el tope del canal y se cortó.
/// </summary>
public sealed class AgentTurn
{
    private AgentTurn()
    {
        SourcesJson = "[]";
    }

    public long Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public int Seq { get; private set; }

    public AgentTurnRoleKind Role { get; private set; }

    public Guid? MessageId { get; private set; }

    public Guid? JobId { get; private set; }

    /// <summary>Los ids citados, como arreglo JSON de textos.</summary>
    public string SourcesJson { get; private set; }

    public bool Truncated { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static AgentTurn Create(
        Guid tenantId, Guid sessionId, int seq, AgentTurnRoleKind role, Guid? messageId, Guid? jobId, IReadOnlyList<string> sources,
        bool truncated, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Seq = seq,
            Role = role,
            MessageId = messageId,
            JobId = jobId,
            SourcesJson = System.Text.Json.JsonSerializer.Serialize(sources),
            Truncated = truncated,
            CreatedAt = now,
        };
}
