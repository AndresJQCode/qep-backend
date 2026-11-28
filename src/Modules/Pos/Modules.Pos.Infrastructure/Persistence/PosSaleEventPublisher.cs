using System.Globalization;
using System.Text.Json;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Escribe los eventos de la venta como <see cref="PosOutboxMessage"/> del mismo <see cref="PosDbContext"/>: salen con el
/// <c>SaveChanges</c> del caso de uso o no salen. La correlación es el id de la venta.
/// </summary>
internal sealed class PosSaleEventPublisher(PosDbContext dbContext) : IPosSaleEventPublisher
{
    public void PublishCreated(PosSale sale, DateTimeOffset occurredAt) =>
        Add(PosSaleEvents.CreatedEventName, sale, JsonSerializer.Serialize(PosSaleEvents.Created(sale, occurredAt)), occurredAt);

    public void PublishVoided(PosSale sale, DateTimeOffset occurredAt) =>
        Add(PosSaleEvents.VoidedEventName, sale, JsonSerializer.Serialize(PosSaleEvents.Voided(sale, occurredAt)), occurredAt);

    private void Add(string eventName, PosSale sale, string payloadJson, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new PosOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = payloadJson,
            CorrelationId = sale.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });
}
