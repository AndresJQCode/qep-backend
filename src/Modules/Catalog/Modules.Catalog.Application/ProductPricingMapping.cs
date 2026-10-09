using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// El único lugar donde el string de <see cref="PriceScaleRequest.Restriction"/> se vuelve
/// <see cref="PriceScaleRestriction"/>. Ningún DTO expone el enum del dominio directamente —
/// mismo criterio que <c>MembershipListItemResponse.State</c>, que viaja como texto y no como
/// el enum de <c>MembershipState</c>.
/// </summary>
internal static class ProductPricingMapping
{
    public static ProductPricing ToDomain(this ProductPricingRequest request) => new()
    {
        // Bridge until Task 4: the HTTP contract still speaks baseUsd/baseCop.
        Prices = LegacyPrices(request.BaseUsd, request.BaseCop),
        Scales = (request.Scales ?? []).Select(ToDomain).ToArray(),
        PackagingUnits = (request.PackagingUnits ?? []).ToArray()
    };

    // Bridge until Task 4.
    private static Dictionary<string, decimal> LegacyPrices(decimal? baseUsd, decimal? baseCop)
    {
        var prices = new Dictionary<string, decimal>();
        if (baseCop is { } cop) prices["COP"] = cop;
        if (baseUsd is { } usd) prices["USD"] = usd;
        return prices;
    }

    // finalUsd/finalCop are still accepted on the wire until Task 4 and ignored (spec D4).
    private static PriceScaleInput ToDomain(PriceScaleRequest request) => new(
        request.FromUnit,
        request.ToUnit,
        request.Discount,
        ParseRestriction(request.Restriction),
        request.Multiple,
        request.AllowGrouping ?? false);

    // Sin mapa por campo a propósito: es el mismo criterio que ya usa el dominio para sus
    // propios códigos — un valor que no es ninguno de los dos válidos no tiene un campo al que
    // "corregir", es un valor que no existe.
    private static PriceScaleRestriction? ParseRestriction(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "multiple" => PriceScaleRestriction.Multiple,
            "packaging_unit" => PriceScaleRestriction.PackagingUnit,
            _ => throw new CatalogDomainException(
                "catalog.product.price_scale.restriction_invalid",
                "The price scale restriction must be 'multiple' or 'packaging_unit'.")
        };

    public static string ToWireValue(this PriceScaleRestriction restriction) => restriction switch
    {
        PriceScaleRestriction.Multiple => "multiple",
        PriceScaleRestriction.PackagingUnit => "packaging_unit",
        _ => throw new ArgumentOutOfRangeException(nameof(restriction), restriction, null)
    };
}
