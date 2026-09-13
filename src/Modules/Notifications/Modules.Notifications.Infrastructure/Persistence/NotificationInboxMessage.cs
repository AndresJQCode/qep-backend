namespace Modules.Notifications.Infrastructure.Persistence;

// Guarda de idempotencia y reclamo por consumidor: una fila por (consumidor, id de mensaje de
// outbox). Esa es la PK, y por eso hay un solo ganador. Mientras ProcessedAt es null, el mensaje está
// reclamado y sin terminar: ClaimedUntil es el lease, y cuando vence otro tick puede retomarlo
// (InboxClaims). Attempts cuenta cuántas veces se reclamó, para cortar un mensaje envenenado.
internal sealed class NotificationInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección de sólo lectura del Outbox de plataforma, consumida independiente por este módulo.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
