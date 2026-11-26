namespace Modules.Quotations.Domain;

/// <summary>
/// El envío del pedido (spec 2026-11-10 §3.3): todo pedido nace <see cref="NotPrepared"/>. Preparar no exige pago
/// (un contraentrega se despacha antes del cobro) y congela el pedido; sólo se deshace si no está facturado.
/// </summary>
public enum OrderFulfillmentStatus
{
    NotPrepared,
    Prepared
}
