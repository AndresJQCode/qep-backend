namespace Modules.Store.Infrastructure.Persistence;

// Proyección del Outbox de plataforma: la escribe StoreOutboxWriter en la unidad de trabajo de Store.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public int Attempts { get; init; }
}
