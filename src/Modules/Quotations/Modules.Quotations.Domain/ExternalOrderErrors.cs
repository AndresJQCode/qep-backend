namespace Modules.Quotations.Domain;

/// <summary>Los códigos del camino externo que cruzan capas: el dominio los lanza y <c>ExternalOrderService</c> los compara para
/// devolverlos como resultado. Viven en Domain para que <see cref="Order"/> lance con la misma constante que el servicio compara:
/// un literal que cambia de un lado solo deja de traducirse, y el rechazo sale como excepción.</summary>
public static class ExternalOrderErrors
{
    /// <summary>Lo lanza <c>QuotationsUnitOfWork</c> al chocar con <c>IX_orders_tenant_external_key</c>. No llega a
    /// HTTP: <c>ExternalOrderService</c> lo resuelve devolviendo el pedido que ya existe.</summary>
    public const string ExternalKeyTaken = "order.order.external_key_taken";

    /// <summary>El de todo cambio a un pedido que ya no está pendiente, no sólo del camino externo.</summary>
    public const string NotPending = "order.order.not_pending";

    public const string SourceInvalid = "order.order.source_invalid";

    public const string StorePickup = "order.order.store_pickup";

    public const string ShippingPartyPresent = "order.order.shipping_party_present";

    public const string ShippingPartyChanged = "order.order.shipping_party_changed";

    public const string ShippingPartyIncomplete = "order.shipping.party_incomplete";
}
