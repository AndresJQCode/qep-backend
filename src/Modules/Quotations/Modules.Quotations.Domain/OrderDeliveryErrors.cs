namespace Modules.Quotations.Domain;

/// <summary>
/// Los códigos de la entrega editable de un pedido (spec 2026-11-24 §10 y §11). Son contrato con el frontend, que los mapea a
/// texto. Los de Shopify los lanza la validación del guardado para que el envío a la tienda nunca falle por un dato que falta;
/// no hay uno de código postal: ningún país lo exige (owner, P-1, 2026-11-25).
/// </summary>
public static class OrderDeliveryErrors
{
    /// <summary>Recoger en tienda con una parte de envío: se rechaza en vez de descartarla, para que el frontend no crea que
    /// guardó una dirección.</summary>
    public const string ShippingPartyOnPickup = "order.delivery.shipping_party_on_pickup";

    /// <summary>Pasar de recoger a enviar con los datos de un cliente que no tiene domicilio.</summary>
    public const string AddressRequired = "order.delivery.address_required";

    public const string ShopifyPartyRequired = "order.delivery.shopify_party_required";

    public const string ShopifyRegionRequired = "order.delivery.shopify_region_required";

    public const string ShopifyRegionUnmapped = "order.delivery.shopify_region_unmapped";
}
