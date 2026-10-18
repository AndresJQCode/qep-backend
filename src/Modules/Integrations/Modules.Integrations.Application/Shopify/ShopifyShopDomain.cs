using System.Text.RegularExpressions;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.3 (D-S33): el dominio de una tienda es <c>^[a-z0-9][a-z0-9-]*\.myshopify\.com$</c> en minúsculas
/// y de a lo sumo <see cref="ShopifyInstall.ShopDomainMaxLength"/> caracteres (el ancho del <c>external_id</c> de la
/// ruta). Es la única puerta: con él se arma la URL de autorización, así que un dominio ajeno a Shopify mandaría a la
/// persona —y el <c>state</c>— a otro host.
/// </summary>
public static partial class ShopifyShopDomain
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\.myshopify\.com\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (candidate.Length > ShopifyInstall.ShopDomainMaxLength || !Shape().IsMatch(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
