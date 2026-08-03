using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

// Demonstrative consumer of tenancy.tenant-settings-updated.v1: it appends a row
// to a projection log. Append-only means a duplicate delivery would insert twice
// unless the Inbox guard suppresses it — which is exactly acceptance #6.
internal sealed class TenantSettingsChangeLogProjection(TenancyDbContext dbContext, IClock clock)
    : IIntegrationEventHandler
{
    public string Consumer => "tenancy.tenant-settings-change-log";

    public string EventName => "tenancy.tenant-settings-updated.v1";

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message.PayloadJson);
        var root = document.RootElement;

        var tenantId = root.GetProperty("tenantId").GetProperty("value").GetGuid();
        var version = root.TryGetProperty("version", out var versionElement)
            ? versionElement.GetInt64()
            : 0;

        dbContext.Set<TenantSettingsChangeLogEntry>().Add(new TenantSettingsChangeLogEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EventId = message.Id,
            Version = version,
            AppliedAt = clock.UtcNow
        });

        return Task.CompletedTask;
    }
}
