namespace Modules.Storage.Infrastructure.Persistence;

// Proyección de escritura del Outbox de plataforma (propiedad de Tenancy). Storage inserta
// acá los eventos de auditoría operativa en la misma transacción que la operación de archivo;
// los consume el worker de proyección del módulo Audit. Mapeado como ExcludeFromMigrations.
internal sealed class StorageOutboxMessage
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
