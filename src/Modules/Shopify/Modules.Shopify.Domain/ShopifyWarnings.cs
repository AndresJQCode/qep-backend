namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-10-15 §5.4 y §6.2.4: los avisos de una importación que sí entró. No detienen el pedido; la bandeja los muestra
/// hasta que alguien los marque como revisados (D-S31).</summary>
public static class ShopifyWarnings
{
    public const string CustomerIncomplete = "customer_incomplete";
    public const string ShippingAddressUnresolved = "shipping_address_unresolved";

    /// <summary>La parte de envío de fuera de Colombia quedó sin región en un país que la tiene: se elige en «Editar pedido».</summary>
    public const string ShippingRegionUnresolved = "shipping_region_unresolved";
    public const string TotalDifference = "total_difference";
    public const string CarrierUnrecognized = "carrier_unrecognized";
    public const string CashOnDeliveryUnmatched = "cash_on_delivery_unmatched";

    public static readonly IReadOnlyList<string> All =
    [
        CustomerIncomplete,
        ShippingAddressUnresolved,
        ShippingRegionUnresolved,
        TotalDifference,
        CarrierUnrecognized,
        CashOnDeliveryUnmatched,
    ];
}
