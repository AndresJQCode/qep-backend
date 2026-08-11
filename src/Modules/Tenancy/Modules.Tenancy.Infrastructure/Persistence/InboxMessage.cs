namespace Modules.Tenancy.Infrastructure.Persistence;

// Registro de deduplicación para la entrega at-least-once de eventos de integración. La
// unicidad por (Consumer, MessageId) garantiza que un consumidor dado aplique un evento
// una sola vez, incluso si se reentrega.
internal sealed class InboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
