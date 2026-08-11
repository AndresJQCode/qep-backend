namespace Modules.Identity.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor: un id de mensaje de outbox ya procesado por el
// consumidor de este módulo. (consumer, message_id) es único.
internal sealed class IdentityInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}

// Proyección de sólo lectura del Outbox de plataforma, consumida independiente por este módulo.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
