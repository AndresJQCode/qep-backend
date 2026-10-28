namespace Modules.Pipeline.Domain;

/// <summary>Spec 2026-10-13 §5.1, D20, D35, D38 y P13 del plan: cuándo una conversación está pendiente de triage.</summary>
public static class TriageRules
{
    /// <summary>Abierta, con cliente, sin negocio abierto del cliente y sin una decisión tomada contra su último entrante
    /// (<paramref name="lastDecidedAgainstInboundAt"/> es el <c>decided_against_inbound_at</c> de la última decisión). Un entrante
    /// nuevo es un instante nuevo: la vuelve pendiente. Sin entrante se compara contra <see cref="TriageDecision.NoInbound"/>.</summary>
    public static bool IsPending(
        bool conversationIsOpen, Guid? customerId, bool customerHasOpenDeal, DateTimeOffset? lastInboundAt, DateTimeOffset? lastDecidedAgainstInboundAt) =>
        conversationIsOpen
        && customerId is not null
        && !customerHasOpenDeal
        && (lastDecidedAgainstInboundAt is not { } decidedAgainst || decidedAgainst < (lastInboundAt ?? TriageDecision.NoInbound));
}

/// <summary>Spec 2026-10-13 §5.2 y D22: el estancado se calcula al leer, sin worker.</summary>
public static class StallRules
{
    /// <summary>Lo más reciente entre el último cambio del negocio (etapa incluida) y el último mensaje de las conversaciones del
    /// cliente, entrante o saliente.</summary>
    public static DateTimeOffset LastActivity(DateTimeOffset dealChangedAt, DateTimeOffset? lastMessageAt) =>
        lastMessageAt is { } message && message > dealChangedAt ? message : dealChangedAt;

    /// <summary>Tiempo transcurrido en UTC, no días calendario del tenant. Una etapa de cierre nunca se estanca.</summary>
    public static bool IsStalled(StageKind kind, int? stallDays, DateTimeOffset lastActivity, DateTimeOffset now) =>
        kind == StageKind.Open && stallDays is { } days && now - lastActivity > TimeSpan.FromDays(days);

    public static int DaysIn(DateTimeOffset enteredAt, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Floor((now - enteredAt).TotalDays));
}
