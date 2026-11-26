namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-24 §11.1: Shopify espera la provincia como código ISO 3166-2 sin el prefijo del país (<c>M</c>, <c>B</c>…) y
/// Geography guarda el código del INE de dos dígitos (<c>Region.Code</c>), así que la tabla vive acá, en código.
/// <c>SpainProvinceCodesGeographyTests</c> la compara contra las regiones sembradas. Las 52 coinciden con los datos de
/// <c>data/regions/ES.yml</c> de <c>Shopify/worldwide</c> (verificado el 2026-11-25, decisión P-4); es la fuente pública de
/// Shopify, no la validación de la Admin API.
/// </summary>
public static class SpainProvinceCodes
{
    /// <summary>Las 52: INE → ISO 3166-2 sin prefijo.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "VI",
        ["02"] = "AB",
        ["03"] = "A",
        ["04"] = "AL",
        ["05"] = "AV",
        ["06"] = "BA",
        ["07"] = "PM",
        ["08"] = "B",
        ["09"] = "BU",
        ["10"] = "CC",
        ["11"] = "CA",
        ["12"] = "CS",
        ["13"] = "CR",
        ["14"] = "CO",
        ["15"] = "C",
        ["16"] = "CU",
        ["17"] = "GI",
        ["18"] = "GR",
        ["19"] = "GU",
        ["20"] = "SS",
        ["21"] = "H",
        ["22"] = "HU",
        ["23"] = "J",
        ["24"] = "LE",
        ["25"] = "L",
        ["26"] = "LO",
        ["27"] = "LU",
        ["28"] = "M",
        ["29"] = "MA",
        ["30"] = "MU",
        ["31"] = "NA",
        ["32"] = "OR",
        ["33"] = "O",
        ["34"] = "P",
        ["35"] = "GC",
        ["36"] = "PO",
        ["37"] = "SA",
        ["38"] = "TF",
        ["39"] = "S",
        ["40"] = "SG",
        ["41"] = "SE",
        ["42"] = "SO",
        ["43"] = "T",
        ["44"] = "TE",
        ["45"] = "TO",
        ["46"] = "V",
        ["47"] = "VA",
        ["48"] = "BI",
        ["49"] = "ZA",
        ["50"] = "Z",
        ["51"] = "CE",
        ["52"] = "ML",
    };

    /// <summary>El código ISO de la provincia con ese código del INE, o <c>null</c> si no es una de las 52.</summary>
    public static string? IsoFor(string ineCode)
    {
        var normalized = ineCode?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : All.GetValueOrDefault(normalized);
    }

    private static readonly Dictionary<string, string> IneByIso =
        All.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>El camino de vuelta: el código del INE de la provincia con ese código ISO (el <c>province_code</c> de Shopify),
    /// o <c>null</c> si no es una de las 52.</summary>
    public static string? IneFor(string? isoCode)
    {
        var normalized = isoCode?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : IneByIso.GetValueOrDefault(normalized);
    }
}
