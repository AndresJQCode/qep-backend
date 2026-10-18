namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2.1 paso 1 y D-S22: el tipo de documento que trae el atributo del pedido, comparado sin mayúsculas con los
/// cuatro que acepta Customers. Uno desconocido no deja el pedido en espera: el importador cae al tipo por defecto de la tienda.
/// Vive en Shopify y no se le pregunta a Customers porque <c>IdentificationTypeParser.Parse</c> lanza con un valor desconocido
/// (H7) y Shopify no referencia Customers.
/// </summary>
public static class ShopifyDocumentTypes
{
    /// <summary>Los valores de Customers, como viajan (<c>IdentificationTypeParser.SupportedWireValues</c>).</summary>
    public static readonly IReadOnlyList<string> Supported = ["NIT", "CC", "CE", "PASAPORTE"];

    /// <summary>El valor de Customers en mayúsculas, o <c>null</c> si no es uno de los cuatro.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : Supported.FirstOrDefault(supported => string.Equals(supported, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
