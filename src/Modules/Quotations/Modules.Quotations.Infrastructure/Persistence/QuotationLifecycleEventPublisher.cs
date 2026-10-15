using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Escribe los eventos del ciclo de cotizaciones y pedidos como <see cref="QuotationsOutboxMessage"/>
/// del mismo <see cref="QuotationsDbContext"/>: salen con el <c>SaveChanges</c> del cambio o no salen.
/// Los nombres de los campos son contrato con Pipeline (<c>QuotationEventPayloads</c>): camelCase y
/// leídos por nombre. La correlación es el id de la cotización en los eventos de cotización y el id
/// del pedido en los de pedido.
/// </summary>
internal sealed class QuotationLifecycleEventPublisher(QuotationsDbContext dbContext) : IQuotationLifecycleEventPublisher
{
    internal const string SentEventName = "quotations.quotation.sent.v1";
    internal const string ConvertedEventName = "quotations.quotation.converted.v1";
    internal const string OrderCreatedEventName = "quotations.order.created.v1";
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

    public void PublishOrderCreated(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCreatedEventName, order.Id.Value, new OrderCreatedPayload(
            order.TenantId, order.Id.Value, order.OrderNumber, order.ClientId, order.AdvisorId.Value, order.Total,
            order.Currency, order.Source.ToString(), order.QuotationId?.Value, occurredAt), occurredAt);
    }

    public void PublishOrderCancelled(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCancelledEventName, order.Id.Value, new OrderCancelledPayload(
            order.TenantId, order.Id.Value, order.QuotationId?.Value, order.Source.ToString(), occurredAt), occurredAt);
    }

    private void Add<TPayload>(string eventName, Guid correlationId, TPayload payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = correlationId.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Los nombres en camelCase son el contrato: System.Text.Json serializa los records con el nombre
    // del parámetro tal cual.
    private sealed record SentPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt);

    private sealed record ConvertedPayload(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid clientId, Guid advisorMemberId,
        decimal total, string currency, DateTimeOffset occurredAt, Guid orderId, string orderNumber);

    private sealed record OrderCreatedPayload(
        Guid tenantId, Guid orderId, string orderNumber, Guid clientId, Guid advisorMemberId, decimal total,
        string currency, string source, Guid? quotationId, DateTimeOffset occurredAt);

    private sealed record OrderCancelledPayload(
        Guid tenantId, Guid orderId, Guid? quotationId, string source, DateTimeOffset occurredAt);
}
