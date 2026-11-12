namespace Modules.Buyers.Infrastructure.Persistence;

// Proyección del Outbox de plataforma: la escribe BuyerOutboxWriter en la unidad de trabajo del llamador.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public int Attempts { get; init; }
}
