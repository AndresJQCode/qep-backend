using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-10-13 §4.5: los tres hechos del ciclo de una cotización que otro módulo necesita —hoy Pipeline, que mueve el
/// negocio con ellos—. Se escriben en el outbox de la misma unidad de trabajo que el cambio: si guardar falla, el evento tampoco
/// existe. Mismo mecanismo que <see cref="IOrderPaymentProofEventPublisher"/>. Los nombres son hechos de Quotations, no órdenes
/// a Pipeline: mañana los puede consumir otro módulo.
/// </summary>
public interface IQuotationLifecycleEventPublisher
{
    /// <summary><c>quotations.quotation.sent.v1</c>. También en un reenvío: el consumidor es idempotente.</summary>
    void PublishSent(Quotation quotation, DateTimeOffset occurredAt);

    /// <summary><c>quotations.quotation.converted.v1</c>. Lleva todo lo de <c>sent</c> para que el consumidor enlace una
    /// cotización convertida que nunca se envió (spec 2026-10-13, D25).</summary>
    void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.cancelled.v1</c>.</summary>
    void PublishOrderCancelled(Order order, DateTimeOffset occurredAt);
}
