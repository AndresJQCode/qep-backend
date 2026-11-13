using Modules.Shopify.Domain;

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

    /// <summary>
    /// Las opciones del select «Atributo del documento»: <see cref="AddressPaths"/> primero, y después los nombres de atributo vistos
    /// en los pedidos de la tienda, tal como <see cref="Read"/> los resuelve. Por eso cada nombre va recortado; dos que sólo difieren
    /// en mayúsculas o espacios se ofrecen una vez (la primera en orden ordinal), porque <see cref="Attribute"/> los lee igual; uno
    /// que se llama como una ruta de dirección no se repite, porque <see cref="Read"/> lo toma por la dirección; y uno más largo que
    /// <see cref="ShopifyStore.AttributeKeyMaxLength"/> se omite, porque el <c>PUT</c> de la tienda no lo guardaría. El orden es
    /// ordinal sin distinguir mayúsculas, para que la lista no dependa del orden en que llegaron los pedidos.
    /// </summary>
    public static IReadOnlyList<string> Options(IEnumerable<string> attributeKeys)
    {
        ArgumentNullException.ThrowIfNull(attributeKeys);
        var attributes = attributeKeys
            .Select(key => key.Trim())
            .Where(key => key.Length is > 0 and <= ShopifyStore.AttributeKeyMaxLength)
            .Where(key => !AddressPaths.Contains(key, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .DistinctBy(key => key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal);
        return [.. AddressPaths, .. attributes];
    }

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
