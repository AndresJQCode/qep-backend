namespace Modules.Geography.Domain;

public readonly record struct BillingCityId(Guid Value)
{
    public static BillingCityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Una ciudad de la lista de Siigo fuera de Colombia: los tres códigos con los que Siigo identifica una parte facturada
/// (<c>country_code</c>, <c>state_code</c>, <c>city_code</c>). Dato de referencia global, sin tenant: lo importa
/// <c>BillingCitySeeder</c> desde un JSON embebido y ningún caso de uso lo crea. Los códigos se guardan tal cual vienen
/// (<c>Us</c>, <c>09</c>, <c>1</c>): la lista no se corrige, aunque traiga rarezas.
/// </summary>
public sealed class BillingCity
{
    public const int CountryNameMaxLength = 80;
    public const int CodeMaxLength = 10;
    public const int NameMaxLength = 120;

    private BillingCity()
    {
    }

    public BillingCityId Id { get; private set; }

    /// <summary>ISO 3166-1 alfa-2 en mayúscula: la forma en la que QEP guarda el país de una empresa.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>El país como lo escribe Siigo (<c>Us</c>): el que viaja en <c>country_code</c>.</summary>
    public string SiigoCountryCode { get; private set; } = string.Empty;

    public string CountryName { get; private set; } = string.Empty;

    public string StateCode { get; private set; } = string.Empty;

    public string StateName { get; private set; } = string.Empty;

    public string CityCode { get; private set; } = string.Empty;

    public string CityName { get; private set; } = string.Empty;

    /// <summary><see cref="CityText.Normalize"/> del nombre: la llave contra la que se compara el texto escrito.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public static BillingCity Create(
        BillingCityId id,
        string siigoCountryCode,
        string countryName,
        string stateCode,
        string stateName,
        string cityCode,
        string cityName)
    {
        // Trim() recorta también el espacio de no separación (U+00A0), que la lista de Siigo trae en algunos nombres.
        var country = siigoCountryCode.Trim();
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
        {
            throw new GeographyDomainException(
                "geography.billing_city.country_invalid",
                "The billing city country must be a two-letter code.");
        }

        return new BillingCity
        {
            Id = id,
            CountryCode = country.ToUpperInvariant(),
            SiigoCountryCode = country,
            CountryName = Field(countryName, CountryNameMaxLength),
            StateCode = Field(stateCode, CodeMaxLength),
            StateName = Field(stateName, NameMaxLength),
            CityCode = Field(cityCode, CodeMaxLength),
            CityName = Field(cityName, NameMaxLength),
            NormalizedName = CityText.Normalize(cityName),
        };
    }

    private static string Field(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new GeographyDomainException(
                "geography.billing_city.field_invalid",
                $"Every billing city field is required and at most {maxLength} characters wide.");
        }

        return trimmed;
    }
}
