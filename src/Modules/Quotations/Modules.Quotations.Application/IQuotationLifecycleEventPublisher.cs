using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Los eventos del ciclo de una cotización y de un pedido, al outbox y en la misma transacción que
/// el cambio (spec 2026-10-13 §4.5 y spec 2026-10-15 §7). El payload es contrato con quien lo
/// consume (Pipeline, Compras y Shopify): se lee por nombre y se cambia en los dos lados.
/// </summary>
public interface IQuotationLifecycleEventPublisher
{
    void PublishSent(Quotation quotation, DateTimeOffset occurredAt);

    void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-10-15 §7: todo pedido nuevo, venga de una cotización, de la pantalla o de una
    /// integración. Lleva <c>source</c> y <c>quotationId</c> (nulo sin cotización).</summary>
    void PublishOrderCreated(Order order, DateTimeOffset occurredAt);

    void PublishOrderCancelled(Order order, DateTimeOffset occurredAt);

    /// <summary>Spec 2026-11-24 §11.1.1: <c>quotations.order.shipping-party-changed.v1</c>: «Editar pedido» cambió la dirección
    /// de envío propia de un pedido de Shopify, y Shopify la encola para mandarla. No lleva la dirección: el envío lee el pedido
    /// al mandar.</summary>
    void PublishShippingPartyChanged(Order order, DateTimeOffset occurredAt);

    /// <summary>Mayorista fase 3 (D-1): <c>quotations.order.wholesale-approved.v1</c> con todas las líneas finales
    /// (<see cref="WholesaleOrderEvents.ApprovedLines"/>); con él la orden de compra del distribuidor queda confirmada.</summary>
    void PublishWholesaleApproved(Order order, DateTimeOffset occurredAt);

    /// <summary>Mayorista fase 3 (D-12, D-21): <c>quotations.order.wholesale-cancelled.v1</c>. <paramref name="byBuyer"/> dice
    /// si lo anuló el distribuidor (su orden ya está anulada) o el mayorista (la orden se anula con el evento).</summary>
    void PublishWholesaleCancelled(Order order, bool byBuyer, DateTimeOffset occurredAt);
}
