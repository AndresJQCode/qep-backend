namespace Modules.Quotations.Domain;

/// <summary>Los códigos del pedido mayorista (mayorista fase 3, «Contrato HTTP»): el dominio los lanza y viajan como 422.</summary>
public static class WholesaleOrderErrors
{
    /// <summary>Aprobar un pedido que no nació de una orden de compra vinculada.</summary>
    public const string SourceInvalid = "order.wholesale.source_invalid";

    public const string AlreadyApproved = "order.wholesale.already_approved";

    /// <summary>Archivar a mano un pedido mayorista que el mayorista todavía no aprueba: saldría de la cuenta de
    /// pendientes y la orden de compra del comprador quedaría en <c>Sent</c> sin salida.</summary>
    public const string AwaitingApproval = "quotation.order.awaiting_wholesale_approval";

    /// <summary>La anulación del comprador mayorista reusa el código de la tienda (convención de
    /// <see cref="Order.CancelByBuyer"/>): no viaja por HTTP, Application lo traduce.</summary>
    public const string NotCancellableByBuyer = BuyerOrderErrors.NotCancellableByBuyer;

    /// <summary>D-4: el precio por línea sólo se edita en un pedido mayorista.</summary>
    public const string UnitPriceNotEditable = "order.item.unit_price_not_editable";
}
