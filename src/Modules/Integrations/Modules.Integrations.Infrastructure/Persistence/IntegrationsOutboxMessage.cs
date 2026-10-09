namespace Modules.Integrations.Infrastructure.Persistence;

// Proyección de escritura del Outbox de plataforma, propiedad de Tenancy. Mapeada como
// ExcludeFromMigrations: Integrations inserta acá y no crea la tabla (copia de PosOutboxMessage).
internal sealed class IntegrationsOutboxMessage
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
