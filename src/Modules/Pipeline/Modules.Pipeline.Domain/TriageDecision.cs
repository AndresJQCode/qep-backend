namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §5.1, D29 y D38: lo que el asesor decidió sobre una conversación. Append-only; vale la última. Guarda el
/// <c>LastInboundAt</c> contra el que se decidió (<see cref="DecidedAgainstInboundAt"/>): el índice único
/// <c>IX_triage_decisions_conversation_inbound</c> sobre ese instante hace que dos asesores que deciden a la vez no dejen dos
/// negocios, y un entrante nuevo permite decidir otra vez.
/// </summary>
public sealed class TriageDecision
{
    /// <summary>D38 y P13: el instante contra el que se decide una conversación sin entrante. La columna no admite nulos porque
    /// Postgres no considera iguales dos NULL en un índice único.</summary>
    public static readonly DateTimeOffset NoInbound = DateTimeOffset.UnixEpoch;

    private TriageDecision()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid CustomerId { get; private set; }

    public TriageDecisionKind Kind { get; private set; }

    public Guid? DealId { get; private set; }

    public Guid? DismissReasonId { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    /// <summary>D38: el <c>LastInboundAt</c> de la conversación cuando se decidió, o <see cref="NoInbound"/>.</summary>
    public DateTimeOffset DecidedAgainstInboundAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public Guid? ActorMemberId { get; private set; }

    public static TriageDecision Created(
        Guid tenantId, Guid conversationId, Guid customerId, Guid dealId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        New(tenantId, conversationId, customerId, TriageDecisionKind.Created, dealId, null, lastInboundAt, actor, now);

    public static TriageDecision Dismissed(
        Guid tenantId, Guid conversationId, Guid customerId, Guid dismissReasonId, DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        New(tenantId, conversationId, customerId, TriageDecisionKind.Dismissed, null, dismissReasonId, lastInboundAt, actor, now);

    private static TriageDecision New(
        Guid tenantId, Guid conversationId, Guid customerId, TriageDecisionKind kind, Guid? dealId, Guid? reasonId,
        DateTimeOffset? lastInboundAt, DealActor actor, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConversationId = conversationId,
            CustomerId = customerId,
            Kind = kind,
            DealId = dealId,
            DismissReasonId = reasonId,
            DecidedAt = now,
            DecidedAgainstInboundAt = lastInboundAt ?? NoInbound,
            ActorKind = actor.Kind,
            ActorMemberId = actor.MemberId,
        };
}
