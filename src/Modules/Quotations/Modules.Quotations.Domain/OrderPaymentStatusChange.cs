namespace Modules.Quotations.Domain;

/// <summary>Un cambio del pago que hizo el sistema, no una persona (spec 2026-11-10 §3.2): de qué estado a cuál, y el
/// neto antes y después de la edición que lo provocó. Lo devuelven <see cref="Order.AddPaymentProofs"/> y
/// <see cref="Order.RecalculatePaymentStatus"/> para que el caso de uso lo deje en el historial.</summary>
public sealed record OrderPaymentStatusChange(
    OrderPaymentStatus From, OrderPaymentStatus To, decimal PreviousNetTotal, decimal NetTotal);
