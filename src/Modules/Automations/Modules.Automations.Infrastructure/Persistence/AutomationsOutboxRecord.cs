namespace Modules.Automations.Infrastructure.Persistence;

// Proyección del outbox de plataforma (Tenancy es dueño de la tabla, ExcludeFromMigrations). Desde el plan 2026-11-18 (Task 12)
// ChatbotMediaReleasePublisher escribe en ella, en la transacción del borrado del chatbot: por eso lleva correlation_id y attempts, que la
// tabla tiene NOT NULL (InitialPlatform), además de las columnas de proceso. Copia de MessagingOutboxRecord.
internal sealed class AutomationsOutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
