using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Spec 2026-10-13 §4.5: acumula el evento en la proyección de outbox de QuotationsDbContext, para que commitee con el cambio.
// Lo consume QuotationEventsProcessor, en Pipeline, que lee el payload por nombre: los campos son contrato.
internal sealed class QuotationLifecycleEventPublisher(QuotationsDbContext dbContext) : IQuotationLifecycleEventPublisher
{
    internal const string SentEventName = "quotations.quotation.sent.v1";

    internal const string ConvertedEventName = "quotations.quotation.converted.v1";

    internal const string OrderCancelledEventName = "quotations.order.cancelled.v1";

    public void PublishSent(Quotation quotation, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        Add(SentEventName, quotation.Id.Value, new SentPayload(
            quotation.TenantId, quotation.Id.Value, quotation.QuotationNumber, quotation.ClientId, quotation.AdvisorId.Value,
            quotation.Total, quotation.Currency, occurredAt), occurredAt);
    }

    public void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ArgumentNullException.ThrowIfNull(order);
        Add(ConvertedEventName, quotation.Id.Value, new ConvertedPayload(
            quotation.TenantId, quotation.Id.Value, quotation.QuotationNumber, quotation.ClientId, quotation.AdvisorId.Value,
            quotation.Total, quotation.Currency, occurredAt, order.Id.Value, order.OrderNumber), occurredAt);
    }

    public void PublishOrderCancelled(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCancelledEventName, order.QuotationId.Value, new OrderCancelledPayload(
            order.TenantId, order.Id.Value, order.QuotationId.Value, occurredAt), occurredAt);
    }

    // La correlación es la cotización en los tres: soporte llega del log del worker de Pipeline a la fila sin adivinar.
    private void Add<TPayload>(string eventName, Guid quotationId, TPayload payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = quotationId.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre con JsonDocument.
    private sealed record SentPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt);

    private sealed record ConvertedPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt, Guid orderId, string orderNumber);

    private sealed record OrderCancelledPayload(Guid tenantId, Guid orderId, Guid quotationId, DateTimeOffset occurredAt);
}
