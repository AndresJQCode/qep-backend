namespace Modules.Geography.Domain;

/// <summary>
/// Un municipio de una <see cref="Region"/> de un país fuera de Colombia. Sólo alimenta el
/// selector: el cliente de afuera guarda el nombre como texto (<c>CityName</c>), nunca este id,
/// que por eso no es una <see cref="CityId"/> ni entra a <c>geography.cities</c>.
/// </summary>
public sealed class RegionCity
{
    private RegionCity()
    {
    }

    private RegionCity(RegionCityId id, RegionId regionId, string code, string name)
    {
        Id = id;
        RegionId = regionId;
        Code = code;
        Name = name;
    }

    public RegionCityId Id { get; private set; }

    public RegionId RegionId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static RegionCity Create(RegionCityId id, RegionId regionId, string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new GeographyDomainException(
                "geography.region_city.code_required", "The city code is required.");
        }

        return new RegionCity(id, regionId, code.Trim(), EnsureName(name));
    }

    public void Rename(string name)
    {
        Name = EnsureName(name);
    }

    private static string EnsureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GeographyDomainException(
                "geography.region_city.name_required", "The city name is required.");
        }

        return name.Trim();
    }
}
