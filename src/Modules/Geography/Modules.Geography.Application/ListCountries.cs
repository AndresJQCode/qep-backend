using BuildingBlocks.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public sealed record ListCountriesQuery : IQuery<IReadOnlyList<CountryDto>>;

public sealed class ListCountriesHandler(ICountryRepository repository)
    : IQueryHandler<ListCountriesQuery, IReadOnlyList<CountryDto>>
{
    public async Task<IReadOnlyList<CountryDto>> HandleAsync(
        ListCountriesQuery query, CancellationToken cancellationToken)
    {
        var countries = await repository.ListAllAsync(cancellationToken);
        return countries
            .OrderBy(country => country.Name, StringComparer.OrdinalIgnoreCase)
            .Select(country => new CountryDto(
                country.Code,
                country.Name,
                CountryCatalogs.Find(country.Code)?.RegionKind.ToString()))
            .ToArray();
    }
}
