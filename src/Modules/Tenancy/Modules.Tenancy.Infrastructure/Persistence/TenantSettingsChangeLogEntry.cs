namespace Modules.Tenancy.Infrastructure.Persistence;

// Proyección append-only alimentada por el evento de integración de tenant-settings. Es
// deliberadamente no idempotente por sí sola (cada aplicación inserta una fila) para que
// la guarda del Inbox sea lo que hace que reprocesar produzca un solo efecto.
internal sealed class TenantSettingsChangeLogEntry
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid EventId { get; init; }

    public long Version { get; init; }

    public DateTimeOffset AppliedAt { get; init; }
}
