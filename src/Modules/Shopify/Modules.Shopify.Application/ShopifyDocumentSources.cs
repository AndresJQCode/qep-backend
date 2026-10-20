namespace Modules.Shopify.Application;

/// <summary>
/// De dónde sale el documento del comprador. La clave de la tienda (<c>document_attribute_key</c>) es una ruta exacta: una de las
/// rutas de dirección de abajo, o el nombre de un atributo del checkout (<c>note_attributes</c>/<c>customAttributes</c>). Si la
/// ruta no existe en el pedido no se encuentra nada; no hay prioridad ni caída de una fuente a la otra, así que la clave
/// <c>company</c> a secas es un atributo llamado «company» y no el campo de la dirección.
/// </summary>
public static class ShopifyDocumentSources
{
    /// <summary>El campo «Empresa» de la dirección de envío, que los checkouts de Colombia usan para el documento.</summary>
    public const string ShippingAddressCompany = "shipping_address.company";

    /// <summary>Las rutas de dirección que se ofrecen en la configuración; el resto de las opciones son atributos.</summary>
    public static IReadOnlyList<string> AddressPaths { get; } = [ShippingAddressCompany];

    public static string? Read(ShopifyOrderSnapshot snapshot, string key)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Equals(key.Trim(), ShippingAddressCompany, StringComparison.Ordinal)
            ? snapshot.ShippingAddress?.Company
            : Attribute(snapshot.Attributes, key);
    }

    internal static string? Attribute(IReadOnlyDictionary<string, string> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var exact))
        {
            return exact;
        }

        var wanted = key.Trim();
        return attributes
            .Where(pair => string.Equals(pair.Key.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }
}
