namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-17 §5.1: los scopes que el módulo mira. <c>write_x</c> cuenta como <c>read_x</c>, la misma regla que el
/// canje de la instalación (<c>CompleteShopifyInstall.EnsureScopes</c>). Comparación ordinal: Shopify los manda en minúsculas.</summary>
public static class ShopifyScopes
{
    public const string ReadProducts = "read_products";

    public const string WriteProducts = "write_products";

    public static bool CanReadProducts(IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        return granted.Any(scope => scope is ReadProducts or WriteProducts);
    }

    /// <summary>El campo <c>scopes</c> de la conexión (separados por coma) como lista; <c>null</c> o vacío, ninguno.</summary>
    public static IReadOnlyCollection<string> Parse(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
