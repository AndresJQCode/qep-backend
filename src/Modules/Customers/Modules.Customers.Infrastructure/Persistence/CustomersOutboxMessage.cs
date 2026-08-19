namespace Modules.Customers.Infrastructure.Persistence;

// Proyeccion de escritura del Outbox de plataforma, propiedad de Tenancy. Customers inserta aca
// los eventos de auditoria operativa en la misma transaccion que el cambio; los consume el worker
// de proyeccion del modulo Audit. Mapeado como ExcludeFromMigrations.
internal sealed class CustomersOutboxMessage
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
