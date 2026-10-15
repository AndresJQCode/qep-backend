using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Los eventos del ciclo de una cotización y de un pedido, al outbox y en la misma transacción que
/// el cambio (spec 2026-10-13 §4.5 y spec 2026-10-15 §7). El payload es contrato con Pipeline: se
/// lee por nombre y se cambia en los dos lados.
/// </summary>
public interface IQuotationLifecycleEventPublisher
{
    void PublishSent(Quotation quotation, DateTimeOffset occurredAt);

    void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-15 §7: todo pedido nuevo, venga de una cotización, de la pantalla o de una
    /// integración. Lleva <c>source</c> y <c>quotationId</c> (nulo sin cotización).</summary>
    void PublishOrderCreated(Order order, DateTimeOffset occurredAt);

    void PublishOrderCancelled(Order order, DateTimeOffset occurredAt);
}
