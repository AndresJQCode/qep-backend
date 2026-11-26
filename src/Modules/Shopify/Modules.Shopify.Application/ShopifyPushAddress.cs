using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// La dirección que <c>orderUpdate</c> manda para la parte de envío de un pedido (spec 2026-11-14 §5 y spec 2026-11-24 §11.1). La
/// arma el envío (<see cref="ShopifyOrderPushProcessor"/>) y con ella el planificador de cambios reconoce el eco de una dirección de
/// fuera de Colombia: una sola regla para lo que sale y para lo que vuelve.
/// <para>El nombre va en <c>firstName</c> y la dirección en <c>address1</c>; la empresa, la del <b>último</b> snapshot, porque
/// <c>orderUpdate</c> reemplaza la dirección entera (Review Focus 3). En Colombia, la ciudad y el departamento con los nombres de QEP
/// y su <c>provinceCode</c>; el país, el del último snapshot si es Colombia. Fuera, el país y la ciudad escrita de la parte y el
/// <c>provinceCode</c> de su región (<see cref="ShopifyShippingAddressRules"/>; ninguno en un país sin regiones). El código postal es
/// el de la parte; sin él, el <c>zip</c> del último snapshot sólo si país y provincia de la parte son los del snapshot; si no,
/// ninguno.</para>
/// </summary>
internal static class ShopifyPushAddress
{
    private const string Colombia = "CO";

    public static bool IsColombia(string? country) =>
        Blank(country) is not { } code || string.Equals(code, Colombia, StringComparison.OrdinalIgnoreCase);

    /// <summary>La dirección a mandar. Lanza si la parte no se puede mandar: el guardado del pedido lo frena antes con las mismas
    /// reglas, y si llegara, no se manda una dirección a medias.</summary>
    public static async Task<ShopifyAddressUpdate> BuildAsync(
        IShopifyGeographyLookup geography,
        ShopifyShippingFingerprint party,
        string? shippingCountry,
        ShopifyAddress? latest,
        CancellationToken cancellationToken)
    {
        if (!IsColombia(party.Country))
        {
            var (foreign, issue) = await ForeignAsync(geography, party, shippingCountry, latest, cancellationToken);
            return foreign ?? throw new InvalidOperationException($"The shipping party cannot be sent to Shopify: {issue}.");
        }

        var departmentId = party.DepartmentId
            ?? throw new InvalidOperationException("A Colombian shipping party has no department.");
        var cityId = party.CityId ?? throw new InvalidOperationException("A Colombian shipping party has no city.");
        var names = await geography.DescribeCityAsync(departmentId, cityId, cancellationToken)
            ?? throw new InvalidOperationException("The city of the shipping party is not in Geography.");
        // El país, como lo escribe el último snapshot si ya es Colombia; si el último está en otro país (la entrega editable
        // devolvió el pedido a Colombia), manda el de la parte.
        var latestCountry = Blank(latest?.CountryCode);
        return Compose(
            party,
            names.City,
            ColombiaProvinceCodes.CodeFor(names.Department),
            latestCountry is not null && IsColombia(latestCountry) ? latestCountry : Colombia,
            shippingCountry,
            latest);
    }

    /// <summary>
    /// Si <paramref name="incoming"/> es lo que el envío de una parte de fuera de Colombia mandó, partiendo de
    /// <paramref name="applied"/> (la versión que Shopify tenía): el eco de ese envío. Una parte que las reglas no dejan mandar no
    /// tiene eco. El teléfono se compara como en la parte (<see cref="ShopifyOrderProjection.SamePhone"/>); país, provincia, ciudad y
    /// código postal, sin distinguir mayúsculas. Sin código postal propio y sin saber qué zip tenía la versión aplicada
    /// (<see cref="ShopifyAddress.ZipUnrecorded"/>), el zip no cuenta.
    /// </summary>
    public static async Task<bool> IsForeignEchoAsync(
        IShopifyGeographyLookup geography,
        ShopifyShippingFingerprint party,
        string? shippingCountry,
        ShopifyAddress? applied,
        ShopifyAddress? incoming,
        CancellationToken cancellationToken)
    {
        if (incoming is null || IsColombia(party.Country) || Blank(party.CityName) is null)
        {
            return false;
        }

        var (sent, _) = await ForeignAsync(geography, party, shippingCountry, applied, cancellationToken);
        if (sent is null)
        {
            return false;
        }

        var zipUnknown = Blank(party.PostalCode) is null && applied is { ZipUnrecorded: true };
        return string.Equals(Joined(sent.FirstName, sent.LastName), Blank(incoming.Name), StringComparison.Ordinal)
            && string.Equals(
                Joined(sent.Address1, sent.Address2), Joined(incoming.Address1, incoming.Address2), StringComparison.Ordinal)
            && Same(sent.City, incoming.City)
            && Same(sent.ProvinceCode, incoming.ProvinceCode)
            && Same(sent.CountryCode, incoming.CountryCode)
            && ShopifyOrderProjection.SamePhone(sent.Phone, incoming.Phone)
            && (zipUnknown || Same(sent.Zip, incoming.Zip));
    }

    // Spec 2026-11-24 §11.1.3. Sin dirección: el motivo por el que las reglas no la dejan mandar.
    private static async Task<(ShopifyAddressUpdate? Address, ShopifyShippingAddressIssue Issue)> ForeignAsync(
        IShopifyGeographyLookup geography,
        ShopifyShippingFingerprint party,
        string? shippingCountry,
        ShopifyAddress? latest,
        CancellationToken cancellationToken)
    {
        var country = party.Country!;
        var region = party.RegionId is { } regionId
            ? await geography.DescribeRegionAsync(regionId, cancellationToken)
            : null;
        // Una región de otro país no es la de esta dirección.
        var regionCode = region is { } found && string.Equals(found.CountryCode, country, StringComparison.OrdinalIgnoreCase)
            ? found.Code
            : null;
        var issue = ShopifyShippingAddressRules.Check(country, regionCode);
        if (issue != ShopifyShippingAddressIssue.None)
        {
            return (null, issue);
        }

        var city = Blank(party.CityName)
            ?? throw new InvalidOperationException("A shipping party outside Colombia has no city name.");
        var provinceCode = ShopifyShippingAddressRules.ProvinceCodeFor(country, regionCode);
        return (Compose(party, city, provinceCode, country, shippingCountry, latest), issue);
    }

    private static ShopifyAddressUpdate Compose(
        ShopifyShippingFingerprint party,
        string city,
        string? provinceCode,
        string countryCode,
        string? shippingCountry,
        ShopifyAddress? latest) =>
        new(
            party.Name ?? string.Empty,
            string.Empty,
            party.Address ?? string.Empty,
            string.Empty,
            city,
            provinceCode,
            countryCode,
            party.Phone ?? string.Empty,
            Blank(latest?.Company),
            Blank(party.PostalCode) ?? KeptZip(latest, shippingCountry, provinceCode));

    // El zip que Shopify ya tiene sigue valiendo mientras la dirección siga en el mismo país y la misma provincia; en otra sería
    // el de otra dirección. Sin provincia de un lado no se puede comprobar, y no viaja.
    private static string? KeptZip(ShopifyAddress? latest, string? partyCountry, string? partyProvinceCode) =>
        latest is not null
        && partyProvinceCode is not null
        && string.Equals(Blank(latest.CountryCode) ?? Colombia, Blank(partyCountry) ?? Colombia, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Blank(latest.ProvinceCode), partyProvinceCode, StringComparison.OrdinalIgnoreCase)
            ? Blank(latest.Zip)
            : null;

    private static bool Same(string? sent, string? received) =>
        string.Equals(Blank(sent), Blank(received), StringComparison.OrdinalIgnoreCase);

    // Las dos partes de un nombre o de una dirección, como un solo texto: Shopify las junta o las reparte.
    private static string? Joined(string? first, string? second) =>
        Blank(string.Join(' ', new[] { Blank(first), Blank(second) }.OfType<string>()));

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
