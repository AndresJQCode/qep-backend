namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.3 y D19: una fila del historial de etapas, append-only. La foto del monto, la moneda y la probabilidad
/// es la del momento: el forecast del slice 2 la necesita así. Sin FK a <c>stages</c>: una etapa borrada no puede borrar historia.
/// <see cref="Ordinal"/> es el orden de escritura dentro del negocio (P4 del plan): lo asigna <see cref="Deal"/>, no la base.
/// </summary>
public sealed class DealStageHistoryEntry
{
    private DealStageHistoryEntry() => Currency = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid DealId { get; private set; }

    public Guid PipelineId { get; private set; }

    /// <summary>1, 2, 3… por negocio, en el orden en que el dominio escribió las filas. Dos filas del mismo SaveChanges comparten
    /// <see cref="OccurredAt"/> y EF no las inserta en orden de <c>Add</c>: sólo este número dice cuál fue primero.</summary>
    public int Ordinal { get; private set; }

    public Guid? FromStageId { get; private set; }

    public Guid ToStageId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public Guid? ActorMemberId { get; private set; }

    public decimal? Amount { get; private set; }

    public string Currency { get; private set; }

    public int Probability { get; private set; }

    internal static DealStageHistoryEntry Create(
        Guid tenantId, Guid dealId, Guid pipelineId, int ordinal, Guid? fromStageId, Guid toStageId, DateTimeOffset occurredAt,
        DealActor actor, decimal? amount, string currency, int probability) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DealId = dealId,
            PipelineId = pipelineId,
            Ordinal = ordinal,
            FromStageId = fromStageId,
            ToStageId = toStageId,
            OccurredAt = occurredAt,
            ActorKind = actor.Kind,
            ActorMemberId = actor.MemberId,
            Amount = amount,
            Currency = currency,
            Probability = probability,
        };
}
