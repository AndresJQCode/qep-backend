using BuildingBlocks.Application;
using Modules.Store.Domain;

namespace Modules.Store.Application;

public sealed record ListPublicDepartmentsQuery(string Host) : IQuery<PublicGeoListDto>;

public sealed record ListPublicCitiesQuery(string Host, Guid DepartmentId) : IQuery<PublicGeoListDto>;

public sealed class ListPublicDepartmentsHandler(StorefrontResolver resolver, IStoreGeography geography)
    : IQueryHandler<ListPublicDepartmentsQuery, PublicGeoListDto>
{
    public async Task<PublicGeoListDto> HandleAsync(ListPublicDepartmentsQuery query, CancellationToken cancellationToken)
    {
        // Sin código DIVIPOLA a propósito: el visitante sólo necesita el id y el nombre. Resolver primero: una tienda sin publicar no
        // sirve ni la geografía.
        _ = await resolver.ResolveAsync(query.Host, cancellationToken);
        var departments = await geography.DepartmentsAsync(cancellationToken);
        return new PublicGeoListDto(departments.Select(item => new PublicGeoItemDto(item.Id, item.Name)).ToList());
    }
}

public sealed class ListPublicCitiesHandler(StorefrontResolver resolver, IStoreGeography geography)
    : IQueryHandler<ListPublicCitiesQuery, PublicGeoListDto>
{
    public async Task<PublicGeoListDto> HandleAsync(ListPublicCitiesQuery query, CancellationToken cancellationToken)
    {
        _ = await resolver.ResolveAsync(query.Host, cancellationToken);
        var cities = await geography.CitiesAsync(query.DepartmentId, cancellationToken)
            ?? throw new ResourceNotFoundException(StoreErrorCodes.PublicDepartmentNotFound, "The department was not found.");
        return new PublicGeoListDto(cities.Select(item => new PublicGeoItemDto(item.Id, item.Name)).ToList());
    }
}
