using BuildingBlocks.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public sealed record ListRegionCitiesQuery(string Country, Guid RegionId)
    : IQuery<IReadOnlyList<RegionCityOptionDto>>;

public sealed class ListRegionCitiesHandler(
    IDepartmentRepository departmentRepository,
    ICityRepository cityRepository,
    IRegionRepository regionRepository)
    : IQueryHandler<ListRegionCitiesQuery, IReadOnlyList<RegionCityOptionDto>>
{
    public const string RegionNotFound = "geography.region.not_found";

    public async Task<IReadOnlyList<RegionCityOptionDto>> HandleAsync(
        ListRegionCitiesQuery query, CancellationToken cancellationToken)
    {
        // Datos globales, sin tenant: una región que no es del país es un 404 sin más que proteger.
        var catalog = CountryCatalogs.Find(query.Country) ?? throw NotFound();

        if (catalog.Country == CountryCatalogs.Colombia)
        {
            var departmentId = new DepartmentId(query.RegionId);
            if (await departmentRepository.FindAsync(departmentId, cancellationToken) is null)
            {
                throw NotFound();
            }

            var cities = await cityRepository.ListByDepartmentAsync(departmentId, cancellationToken);
            return cities
                .Select(city => new RegionCityOptionDto(city.Id.Value, city.DivipolaCode, city.Name))
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var regionId = new RegionId(query.RegionId);
        var region = await regionRepository.FindAsync(regionId, cancellationToken);
        if (region is null || region.CountryCode != catalog.Country)
        {
            throw NotFound();
        }

        var regionCities = await regionRepository.ListCitiesAsync(regionId, cancellationToken);
        return regionCities
            .Select(city => new RegionCityOptionDto(city.Id.Value, city.Code, city.Name))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ResourceNotFoundException NotFound() =>
        new(RegionNotFound, "The region does not exist for that country.");
}
