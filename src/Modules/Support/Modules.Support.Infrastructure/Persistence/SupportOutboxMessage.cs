namespace Modules.Support.Infrastructure.Persistence;

// Copia de GeographyOutboxMessage: una fila de platform.outbox_messages escrita en la transacción de Soporte (spec 2026-11-22 §6 D-3).
internal sealed class SupportOutboxMessage
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
