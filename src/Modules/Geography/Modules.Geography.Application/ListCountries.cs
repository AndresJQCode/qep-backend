using BuildingBlocks.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public sealed record ListCountriesQuery : IQuery<IReadOnlyList<CountryCatalogDto>>;

public sealed class ListCountriesHandler
    : IQueryHandler<ListCountriesQuery, IReadOnlyList<CountryCatalogDto>>
{
    public Task<IReadOnlyList<CountryCatalogDto>> HandleAsync(
        ListCountriesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CountryCatalogDto>>(CountryCatalogs.All
            .Select(catalog => new CountryCatalogDto(catalog.Country, catalog.RegionKind.ToString()))
            .ToArray());
}
