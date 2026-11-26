namespace Modules.Quotations.Domain;

/// <summary>
/// El estado del pedido es uno de cuatro ejes independientes (spec 2026-11-10), al estilo de Shopify: éste dice si
/// al pedido le queda trabajo; el pago (<see cref="OrderPaymentStatus"/>), el envío (<see cref="OrderFulfillmentStatus"/>)
/// y la marca de facturado (<c>Order.InvoicedAt</c>) van aparte.
///
/// Todo pedido nace <see cref="Open"/>. <see cref="Archived"/> es «terminado»: sale de la vista por defecto y se
/// desarchiva a mano. <see cref="Cancelled"/> es terminal y guarda quién, cuándo y por qué; no se anula un pedido
/// facturado ni uno preparado, salvo la anulación que llega de la plataforma (<c>Order.CancelFromPlatform</c>).
///
/// La columna guarda el nombre (<c>HasConversion&lt;string&gt;()</c>), así que el orden no importa para los datos.
/// </summary>
public enum OrderStatus
{
    Open,
    Archived,
    Cancelled
}
