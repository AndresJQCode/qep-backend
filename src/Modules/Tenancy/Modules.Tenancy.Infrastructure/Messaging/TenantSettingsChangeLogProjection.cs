using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

// Consumidor demostrativo de tenancy.tenant-settings-updated.v1: agrega una fila
// a un log de proyección. Al ser append-only, una entrega duplicada insertaría dos veces
// salvo que la guarda del Inbox la suprima — que es justamente la aceptación #6.
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
