using System.Globalization;
using System.Text.Json;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Infrastructure.Persistence;

/// <summary>
/// Escribe <c>purchases.order.received.v1</c> como <see cref="PurchasesOutboxRecord"/> del mismo
/// <see cref="PurchasesDbContext"/>: sale con el <c>SaveChanges</c> del cambio o no sale. La correlación es el id de la orden.
/// </summary>
internal sealed class PurchaseOrderEventPublisher(PurchasesDbContext dbContext) : IPurchaseOrderEventPublisher
{
    public void PublishReceived(PurchaseOrder order, string origin, DateTimeOffset occurredAt)
    {
        var payload = PurchaseOrderReceivedEvent.From(order, origin, occurredAt);
        dbContext.Outbox.Add(new PurchasesOutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = PurchaseOrderReceivedEvent.EventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = order.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });
    }
}
