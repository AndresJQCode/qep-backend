namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-26 §4.2: la dirección de muestra de una ciudad sin resolver («Cra 5 # 10-20, CALLI, Valle del Cauca, CO»). Se ve en
/// la consola del operador, que cruza tenants, así que lleva sólo la dirección: nunca el nombre ni el teléfono del comprador, que
/// sí van en la línea cruda de las notas del pedido (<see cref="ShopifyOrderImporter.RawAddressLabel"/>), porque esas son del
/// tenant. La usan la importación y el recorrido de la historia previa, para que las dos muestras tengan la misma forma.
/// </summary>
public static class ShopifyUnresolvedCitySample
{
    /// <summary><c>address1, address2, city, province, countryCode</c>, recortados, sin los vacíos y unidos con <c>", "</c>.</summary>
    public static string Of(ShopifyAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return string.Join(", ", new[] { address.Address1, address.Address2, address.City, address.Province, address.CountryCode }
            .Select(part => part?.Trim())
            .Where(part => !string.IsNullOrEmpty(part)));
    }
}
