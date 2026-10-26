namespace Modules.Geography.Infrastructure.Persistence;

// Copia de QuotationsOutboxMessage: una fila de platform.outbox_messages escrita en la transacción de Geography.
internal sealed class GeographyOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
