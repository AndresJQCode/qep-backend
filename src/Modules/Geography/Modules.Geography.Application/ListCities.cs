using BuildingBlocks.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public sealed record ListCitiesQuery(DepartmentId DepartmentId) : IQuery<IReadOnlyList<CityDto>>;

public sealed class ListCitiesHandler(ICityRepository repository)
    : IQueryHandler<ListCitiesQuery, IReadOnlyList<CityDto>>
{
    public async Task<IReadOnlyList<CityDto>> HandleAsync(
        ListCitiesQuery query, CancellationToken cancellationToken)
    {
        var cities = await repository.ListByDepartmentAsync(query.DepartmentId, cancellationToken);
        return cities
            .OrderBy(city => city.Name, StringComparer.OrdinalIgnoreCase)
            .Select(city => new CityDto(
                city.Id.Value, city.DivipolaCode, city.Name, city.DepartmentId.Value))
            .ToArray();
    }
}
