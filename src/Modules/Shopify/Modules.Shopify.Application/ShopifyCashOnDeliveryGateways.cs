using System.Globalization;
using System.Text;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2.2 paso 6: las pasarelas de pago de Shopify que significan contraentrega. Se comparan normalizadas igual
/// que los nombres de transportadora (<c>CarrierName.Normalize</c> de Quotations: sin mayúsculas, tildes ni espacios repetidos);
/// Shopify no referencia Quotations, así que la regla está copiada acá y la amarra <c>ShopifyShippingMapperTests</c>. Los valores
/// son los iniciales del spec, a confirmar con un pedido contraentrega real (V-8).
/// </summary>
public static class ShopifyCashOnDeliveryGateways
{
    /// <summary>Ya normalizados.</summary>
    public static readonly IReadOnlyList<string> Values =
    [
        "cash on delivery (cod)",
        "contra entrega",
        "contraentrega",
        "pago contra entrega",
    ];

    /// <summary><c>true</c> si alguna de las pasarelas del pedido es de contraentrega.</summary>
    public static bool Matches(IEnumerable<string> gateways)
    {
        ArgumentNullException.ThrowIfNull(gateways);
        return gateways
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway))
            .Any(gateway => Values.Contains(Normalize(gateway), StringComparer.Ordinal));
    }

    /// <summary>El mismo algoritmo que <c>CarrierName.Normalize</c>.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var stripped = new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        return stripped.Normalize(NormalizationForm.FormC);
    }
}
