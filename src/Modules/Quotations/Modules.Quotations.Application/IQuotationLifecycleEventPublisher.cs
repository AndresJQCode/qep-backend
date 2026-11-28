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

    /// <summary>
    /// <c>quotations.order.cancelled.v1</c>. <paramref name="fromPlatform"/> es <c>true</c> sólo cuando la anulación llega de la
    /// plataforma (<see cref="Order.CancelFromPlatform"/>, Shopify): esa pasa por encima de «preparado» y «archivado», y el
    /// inventario no devuelve lo ya descontado. Se llama justo después de la mutación: <c>version</c> es la que ella dejó.
    /// </summary>
    void PublishOrderCancelled(Order order, bool fromPlatform, DateTimeOffset occurredAt);

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

    // Inventario básico (spec 2026-11-27 §5.1): los cinco eventos que el WMS consume para reservar, descontar y devolver stock.
    // Cada uno se llama justo después de la mutación que lo causa, no al final del caso de uso: así dos eventos del mismo caso
    // de uso (preparar y archivar solo) llevan versiones distintas y crecientes.

    /// <summary><c>quotations.order.edited.v1</c>: las líneas del pedido cambiaron; viajan todas, como quedaron.</summary>
    void PublishOrderEdited(Order order, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.prepared.v1</c>. <paramref name="origin"/> es un <see cref="OrderEventOrigins"/>: el
    /// consumidor sólo descuenta con <c>Manual</c>; <c>WmsPacking</c> (T7) ya descontó por su lado.</summary>
    void PublishOrderPrepared(Order order, string origin, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.unprepared.v1</c>: sin líneas; el consumidor compensa lo que él mismo asentó.</summary>
    void PublishOrderUnprepared(Order order, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.archived.v1</c>, con <c>isPrepared</c> (archivar uno preparado no mueve nada) y las líneas.</summary>
    void PublishOrderArchived(Order order, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.unarchived.v1</c>, con el mismo <c>isPrepared</c> de su archivo: un archivado no puede
    /// prepararse ni deshacerse, así que la marca no cambia mientras está archivado.</summary>
    void PublishOrderUnarchived(Order order, DateTimeOffset occurredAt);
}

/// <summary>Quién originó el <c>prepared</c>: la consola (<c>Manual</c>) o el empaque con la app del WMS (<c>WmsPacking</c>, T7).</summary>
public static class OrderEventOrigins
{
    public const string Manual = "Manual";

    public const string WmsPacking = "WmsPacking";
}
