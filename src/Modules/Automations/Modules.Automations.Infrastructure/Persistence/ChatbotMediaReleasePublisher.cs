using System.Text.Json;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

// Copia de TemplateHeaderImageReleasePublisher (Messaging): acumula el aviso en la proyección del outbox de AutomationsDbContext para que
// commitee atómico con el borrado del chatbot (AutomationsUnitOfWork.SaveChangesAsync). Lo consume Storage (storage.chatbot-media-release).
internal sealed class ChatbotMediaReleasePublisher(AutomationsDbContext dbContext) : IChatbotMediaReleases
{
    public void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt) =>
        dbContext.OutboxMessages.Add(new AutomationsOutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = AutomationsIntegrationEvents.ChatbotMediaReleased,
            PayloadJson = JsonSerializer.Serialize(new ReleasedPayload(tenantId, fileIds)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
            Attempts = 0,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre, sin opciones de serialización.
    private sealed record ReleasedPayload(Guid tenantId, IReadOnlyCollection<Guid> fileIds);
}
