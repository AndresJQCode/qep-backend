using System.Text.Json;
using Modules.Catalog.Application;

namespace Modules.Catalog.Infrastructure.Persistence;

// Acumula el aviso en la proyección de outbox de CatalogDbContext, para que commitee atómico con
// el cambio del producto. Lo consume Storage (storage.product-image-release).
internal sealed class ProductImageReleasePublisher(CatalogDbContext dbContext)
    : IProductImageReleasePublisher
{
    public void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt)
    {
        var distinct = fileIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return;
        }

        dbContext.Outbox.Add(new CatalogOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = CatalogIntegrationEvents.ProductImagesReleased,
            PayloadJson = JsonSerializer.Serialize(new ImagesReleasedPayload(tenantId, distinct)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre, sin opciones de serialización de por medio.
    private sealed record ImagesReleasedPayload(Guid tenantId, IReadOnlyList<Guid> fileIds);
}
