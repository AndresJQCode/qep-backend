namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-11-14 §3 y §4: los motivos de un cambio de Shopify, en <c>snake_case</c>. Son contrato con el diccionario del
/// frontend: no se renombran.
/// <list type="bullet">
/// <item>Bloqueo del cambio entero (<c>order_edits.status_reason</c>): <see cref="OrderNotEditable"/>,
/// <see cref="PaymentStatusNotAllowed"/> y <see cref="ProductUnmatched"/>.</item>
/// <item>Conflicto de un grupo (<c>groups[].reason</c>): el resto, más los motivos de envío del importador
/// (<c>freight_tax_rate_missing</c>, …) tal cual.</item>
/// </list>
/// </summary>
public static class ShopifyEditReasons
{
    public const string OrderNotEditable = "order_not_editable";
    public const string PaymentStatusNotAllowed = "payment_status_not_allowed";
    public const string ProductUnmatched = "product_unmatched";

    public const string QepChanged = "qep_changed";
    public const string CustomerChanged = "customer_changed";
    public const string CityUnresolved = "city_unresolved";
    public const string PartyIncomplete = "party_incomplete";
    public const string StorePickup = "store_pickup";
    public const string StorePickupChanged = "store_pickup_changed";
}
