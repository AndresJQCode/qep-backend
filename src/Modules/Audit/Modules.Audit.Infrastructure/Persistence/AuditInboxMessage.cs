namespace Modules.Audit.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor para la proyección de auditoría operativa: un id de
// mensaje de outbox ya procesado por este módulo. (consumer, message_id) es único.
internal sealed class AuditInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}

// Proyección de sólo lectura del Outbox de plataforma, consumida de forma independiente por
// este módulo para proyectar eventos de auditoría operativa en audit.entries.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
