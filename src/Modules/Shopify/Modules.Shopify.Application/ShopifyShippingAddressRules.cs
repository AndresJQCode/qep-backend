namespace Modules.Shopify.Application;

/// <summary>Por qué una dirección de envío no se puede mandar a Shopify.</summary>
public enum ShopifyShippingAddressIssue
{
    None,
    RegionRequired,
    RegionUnmapped,
}

/// <summary>
/// Spec 2026-11-24 §11.1.4: qué dirección de envío puede mandar el worker. Las usan los dos lados con las mismas reglas: el envío
/// (<see cref="ShopifyOrderPushProcessor"/>) y el guardado del pedido (<c>IExternalOrderDeliveryCheck</c>, adaptador en
/// Bootstrapper), que la rechaza antes para que el worker no falle por un dato que falta.
/// <para>Colombia va por su ciudad DIVIPOLA y siempre sirve. Un país con regiones en Geography (hoy España) necesita la región, y
/// su tabla a los códigos de Shopify. Un país sin regiones se manda sin <c>provinceCode</c> y decide Shopify. El código postal
/// <b>nunca</b> es motivo de rechazo, en ningún país (owner, P-1, 2026-11-25).</para>
/// </summary>
public static class ShopifyShippingAddressRules
{
    private const string Colombia = "CO";

    private const string Spain = "ES";

    // Los países cuyas regiones siembra Geography: sin región no hay provinceCode que mandar.
    private static readonly HashSet<string> CountriesWithCatalog = new(StringComparer.OrdinalIgnoreCase) { Spain };

    /// <param name="country">ISO 3166-1 alfa-2 de la parte.</param>
    /// <param name="regionCode">El código oficial de la región de la parte en su país (Geography), o <c>null</c> sin región.</param>
    public static ShopifyShippingAddressIssue Check(string country, string? regionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        if (string.Equals(country.Trim(), Colombia, StringComparison.OrdinalIgnoreCase))
        {
            return ShopifyShippingAddressIssue.None;
        }

        if (string.IsNullOrWhiteSpace(regionCode))
        {
            return HasRegionCatalog(country)
                ? ShopifyShippingAddressIssue.RegionRequired
                : ShopifyShippingAddressIssue.None;
        }

        // Una región de un país sin tabla (con catálogo en Geography y sin traducción acá) tampoco se puede mandar.
        return ProvinceCodeFor(country, regionCode) is null
            ? ShopifyShippingAddressIssue.RegionUnmapped
            : ShopifyShippingAddressIssue.None;
    }

    /// <summary>Si Geography siembra las regiones de ese país.</summary>
    public static bool HasRegionCatalog(string country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        return CountriesWithCatalog.Contains(country.Trim());
    }

    /// <summary>El inverso de <see cref="ProvinceCodeFor"/>: el código oficial de la región (Geography) para un
    /// <c>provinceCode</c> de Shopify, o <c>null</c> si el país no tiene tabla o el código no está en ella.</summary>
    public static string? RegionCodeFor(string country, string? provinceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        return string.Equals(country.Trim(), Spain, StringComparison.OrdinalIgnoreCase)
            ? SpainProvinceCodes.IneFor(provinceCode)
            : null;
    }

    /// <summary>El <c>provinceCode</c> de Shopify de una región fuera de Colombia, o <c>null</c> si el país no tiene tabla o la
    /// región no está en ella.</summary>
    public static string? ProvinceCodeFor(string country, string? regionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        return regionCode is not null && string.Equals(country.Trim(), Spain, StringComparison.OrdinalIgnoreCase)
            ? SpainProvinceCodes.IsoFor(regionCode)
            : null;
    }
}
