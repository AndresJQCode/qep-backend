namespace Modules.Storage.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor (spec 2026-09-16, D9): un mensaje del outbox de plataforma
// que un consumidor de Storage ya procesó. (consumer, message_id) es única. Mismo diseño que
// IdentityInboxMessage.
internal sealed class StorageInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
