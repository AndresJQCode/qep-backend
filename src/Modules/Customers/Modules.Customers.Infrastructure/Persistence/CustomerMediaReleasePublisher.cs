using System.Text.Json;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Persistence;

// Acumula el aviso en la proyección de outbox de CustomersDbContext, para que commitee atómico con
// el borrado del cliente. Lo consume Messaging (messaging.customer-media-purge).
internal sealed class CustomerMediaReleasePublisher(CustomersDbContext dbContext)
    : ICustomerMediaReleasePublisher
{
    public void Publish(Guid tenantId, Guid customerId, IReadOnlyCollection<string> storageKeys, DateTimeOffset occurredAt)
    {
        var distinct = storageKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return;
        }

        dbContext.Outbox.Add(new CustomersOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = CustomersIntegrationEvents.CustomerMediaReleased,
            PayloadJson = JsonSerializer.Serialize(new MediaReleasedPayload(tenantId, customerId, distinct)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre, sin opciones de serialización de por medio.
    private sealed record MediaReleasedPayload(Guid tenantId, Guid customerId, IReadOnlyList<string> storageKeys);
}
