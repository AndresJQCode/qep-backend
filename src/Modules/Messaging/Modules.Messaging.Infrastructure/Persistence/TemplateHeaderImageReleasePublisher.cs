using System.Text.Json;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

// Copia de ProductImageReleasePublisher (Catalog): acumula el aviso en la proyección del outbox de MessagingDbContext para que commitee
// atómico con la eliminación de la fila. Lo consume Storage (storage.template-header-image-release).
internal sealed class TemplateHeaderImageReleasePublisher(MessagingDbContext dbContext) : ITemplateHeaderImageReleases
{
    public void Publish(Guid tenantId, Guid fileId, DateTimeOffset occurredAt) =>
        dbContext.OutboxMessages.Add(new MessagingOutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = MessagingIntegrationEvents.TemplateHeaderImageReleased,
            PayloadJson = JsonSerializer.Serialize(new ReleasedPayload(tenantId, fileId)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
            Attempts = 0,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre, sin opciones de serialización.
    private sealed record ReleasedPayload(Guid tenantId, Guid fileId);
}
