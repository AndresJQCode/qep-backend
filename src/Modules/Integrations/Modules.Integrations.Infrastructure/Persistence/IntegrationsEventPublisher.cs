using System.Text.Json;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-08, «Eventos de outbox»: misma transacción que el cambio (P2).</summary>
internal sealed class IntegrationsEventPublisher(IntegrationsDbContext dbContext) : IConnectionEventPublisher
{
    public void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new IntegrationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(new ConnectionEventPayload(
                connection.TenantId, connection.Id, connection.ProviderKey, occurredAt)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox; nunca un valor de campo.
    private sealed record ConnectionEventPayload(
        Guid tenantId, Guid connectionId, string providerKey, DateTimeOffset occurredAt);
}
