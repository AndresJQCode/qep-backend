namespace Modules.Tenancy.Infrastructure.Persistence;

// Append-only projection fed by the tenant-settings integration event. It is
// deliberately non-idempotent on its own (each apply inserts a row) so that the
// Inbox guard is what makes reprocessing produce a single effect.
internal sealed class TenantSettingsChangeLogEntry
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid EventId { get; init; }

    public long Version { get; init; }

    public DateTimeOffset AppliedAt { get; init; }
}
