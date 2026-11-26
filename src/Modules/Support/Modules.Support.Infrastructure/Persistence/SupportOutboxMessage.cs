namespace Modules.Support.Infrastructure.Persistence;

// Proyección de escritura del outbox de la plataforma, cuya dueña es Tenancy (copia de CatalogOutboxMessage). Soporte escribe acá los
// eventos de integración que otros módulos consumen (la campana de Notifications, spec 2026-11-22 §12; la tarea asignada, spec de
// tareas internas §6 D-3), en la misma transacción que el cambio que los causa. Mapeada con ExcludeFromMigrations: no crea tabla.
internal sealed class SupportOutboxMessage
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
