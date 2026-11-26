namespace Modules.Quotations.Domain;

/// <summary>Lo que cruza capas en la cancelación que hace el comprador desde la tienda: el dominio lanza con el código y
/// Application lo traduce a 409.</summary>
public static class BuyerOrderErrors
{
    public const string NotCancellableByBuyer = "order.order.not_cancellable_by_buyer";

    public const string CancellationReason = "Cancelado por el comprador desde la tienda";
}
