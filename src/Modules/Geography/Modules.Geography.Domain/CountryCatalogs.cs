namespace Modules.Geography.Domain;

/// <summary>Cómo se llama la división de primer nivel de un país. Viaja como nombre de enum: el diccionario lo tiene el frontend.</summary>
public enum RegionKind
{
    Department,
    Province,
}

/// <summary>Un país que tiene catálogo de regiones y ciudades.</summary>
public sealed record CountryCatalog(string Country, RegionKind RegionKind);

/// <summary>
/// La única lista de países con catálogo. Un país nuevo es una entrada más de <see cref="All"/> y
/// su archivo de datos; ver <c>RegionSeeder</c>. Colombia se lee de DIVIPOLA y el resto de
/// <see cref="Region"/>.
/// </summary>
public static class CountryCatalogs
{
    public const string Colombia = "CO";
    public const string Spain = "ES";

    public static IReadOnlyList<CountryCatalog> All { get; } =
    [
        new(Colombia, RegionKind.Department),
        new(Spain, RegionKind.Province),
    ];

    /// <summary>El país se recorta y se pasa a mayúsculas, como en <c>DocumentTypesByCountry</c>.</summary>
    private static string Normalize(string country) => country.Trim().ToUpperInvariant();

    /// <summary>El catálogo del país, o null si no tiene.</summary>
    public static CountryCatalog? Find(string? country) =>
        string.IsNullOrWhiteSpace(country)
            ? null
            : All.FirstOrDefault(catalog => catalog.Country == Normalize(country));
}
