namespace Modules.Catalog.Domain;

/// <summary>
/// El nombre visible de una variante (spec §3.4): el del producto si no tiene valores de opción,
/// y si los tiene, <c>«{producto} – {valor1} / {valor2} / {valor3}»</c> en el orden de las
/// opciones —mismo formato que el título de línea de un pedido de Shopify—. Es lo que se guarda
/// como foto en las líneas y lo que muestran PDF, Excel, POS y reportes, así que se arma sólo acá.
/// </summary>
public static class VariantDisplayName
{
    // Guion largo (U+2013) con un espacio a cada lado.
    private const string Separator = " – ";

    public static string For(string productName, IReadOnlyList<string> valueNames)
    {
        ArgumentNullException.ThrowIfNull(productName);
        ArgumentNullException.ThrowIfNull(valueNames);

        return valueNames.Count == 0
            ? productName
            : productName + Separator + string.Join(" / ", valueNames);
    }
}
