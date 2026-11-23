namespace Modules.Geography.Domain;

/// <summary>
/// La división de primer nivel de un país **fuera de Colombia** (provincia de España, por ejemplo).
/// Dato de referencia global, sin tenant: lo importa <c>RegionSeeder</c> desde un JSON embebido y
/// no lo crea ningún caso de uso. Colombia no pasa por acá: sus departamentos viven en
/// <see cref="Department"/> con el código DIVIPOLA, y los endpoints por país los leen de allá.
/// </summary>
public sealed class Region
{
    private Region()
    {
    }

    private Region(RegionId id, string countryCode, string code, string name)
    {
        Id = id;
        CountryCode = countryCode;
        Code = code;
        Name = name;
    }

    public RegionId Id { get; private set; }

    /// <summary>ISO 3166-1 alfa-2, en mayúscula: el mismo formato que <c>Company.Country</c>.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>El código oficial de la región en su país (el INE en España: 2 dígitos).</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static Region Create(RegionId id, string countryCode, string code, string name)
    {
        if (countryCode is not { Length: 2 } || !countryCode.All(char.IsAsciiLetterUpper))
        {
            throw new GeographyDomainException(
                "geography.region.country_invalid",
                "The region country must be an uppercase ISO 3166-1 alpha-2 code.");
        }

        return new Region(id, countryCode, EnsureCode(code), EnsureName(name));
    }

    // Lo usa el importador cuando el nombre de un código ya existente cambia de un año a otro.
    public void Rename(string name)
    {
        Name = EnsureName(name);
    }

    private static string EnsureCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new GeographyDomainException(
                "geography.region.code_required", "The region code is required.");
        }

        return code.Trim();
    }

    private static string EnsureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GeographyDomainException(
                "geography.region.name_required", "The region name is required.");
        }

        return name.Trim();
    }
}
