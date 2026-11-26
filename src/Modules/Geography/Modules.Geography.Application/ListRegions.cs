using BuildingBlocks.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public sealed record ListRegionsQuery(string Country) : IQuery<IReadOnlyList<RegionOptionDto>>;

public sealed class ListRegionsHandler(
    IDepartmentRepository departmentRepository, IRegionRepository regionRepository)
    : IQueryHandler<ListRegionsQuery, IReadOnlyList<RegionOptionDto>>
{
    public async Task<IReadOnlyList<RegionOptionDto>> HandleAsync(
        ListRegionsQuery query, CancellationToken cancellationToken)
    {
        // Un país sin catálogo no es un error: ver RegionOptionDto.
        if (CountryCatalogs.Find(query.Country) is not { } catalog)
        {
            return [];
        }

        if (catalog.Country == CountryCatalogs.Colombia)
        {
            var departments = await departmentRepository.ListAllAsync(cancellationToken);
            return departments
                .Select(department => new RegionOptionDto(
                    department.Id.Value, department.DivipolaCode, department.Name))
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var regions = await regionRepository.ListByCountryAsync(catalog.Country, cancellationToken);
        return regions
            .Select(region => new RegionOptionDto(region.Id.Value, region.Code, region.Name))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
