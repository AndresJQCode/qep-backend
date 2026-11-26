using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-24 «Shopify: parte de envío de pedidos de otros países»: el destino de una dirección de envío de fuera de
/// Colombia. Una sola regla para el importador (<see cref="ShopifyOrderImporter"/>) y el planificador de cambios
/// (<see cref="ShopifyEditPlanner"/>); la de salida a Shopify es <see cref="ShopifyPushAddress"/>.
/// </summary>
internal static class ShopifyForeignAddress
{
    /// <summary>Con país y distinto de <c>CO</c> (<see cref="ShopifyPushAddress.IsColombia"/>: sin país cuenta como Colombia).</summary>
    public static bool IsForeign(ShopifyAddress? address) =>
        address is not null && !ShopifyPushAddress.IsColombia(address.CountryCode);

    /// <summary>
    /// El país en mayúscula, la ciudad escrita recortada y la región, si el país tiene catálogo en Geography y la provincia se
    /// traduce y existe. <c>null</c> sin ciudad, o con una de más de <see cref="ShopifyCityCorrection.PartyCityNameMaxLength"/>
    /// caracteres: recortarla daría otro texto, que además podría volver a Shopify. Sólo para una dirección
    /// <see cref="IsForeign"/>.
    /// </summary>
    public static async Task<ShopifyForeignDestination?> ResolveAsync(
        IShopifyGeographyLookup geography, ShopifyAddress address, CancellationToken cancellationToken)
    {
        if (address.City?.Trim() is not { Length: > 0 and <= ShopifyCityCorrection.PartyCityNameMaxLength } city)
        {
            return null;
        }

        var country = address.CountryCode!.Trim().ToUpperInvariant();
        if (!ShopifyShippingAddressRules.HasRegionCatalog(country))
        {
            return new ShopifyForeignDestination(country, city, null, RegionUnresolved: false);
        }

        var regionId = ShopifyShippingAddressRules.RegionCodeFor(country, address.ProvinceCode) is { } code
            ? await geography.FindRegionAsync(country, code, cancellationToken)
            : null;
        return new ShopifyForeignDestination(country, city, regionId, RegionUnresolved: regionId is null);
    }
}

/// <summary><see cref="RegionUnresolved"/>: el país tiene catálogo y no se encontró la región; alguien tiene que elegirla.</summary>
internal sealed record ShopifyForeignDestination(string Country, string CityName, Guid? RegionId, bool RegionUnresolved);
