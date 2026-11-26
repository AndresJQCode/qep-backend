using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record ListPublicSupportDepartmentsQuery(string? TenantSlug) : IQuery<SupportPublicGeoListDto>;

public sealed record ListPublicSupportCitiesQuery(string? TenantSlug, Guid DepartmentId) : IQuery<SupportPublicGeoListDto>;

/// <summary>Los departamentos de Colombia para el formulario (plan F19). Resuelve el tenant primero: un formulario que no se sirve
/// tampoco sirve su geografía.</summary>
public sealed class ListPublicSupportDepartmentsHandler(SupportPublicResolver resolver, ISupportGeography geography)
    : IQueryHandler<ListPublicSupportDepartmentsQuery, SupportPublicGeoListDto>
{
    public async Task<SupportPublicGeoListDto> HandleAsync(ListPublicSupportDepartmentsQuery query, CancellationToken cancellationToken)
    {
        _ = await resolver.ResolveAsync(query.TenantSlug, cancellationToken);
        return ToDto(await geography.DepartmentsAsync(cancellationToken));
    }

    internal static SupportPublicGeoListDto ToDto(IReadOnlyList<SupportGeoItem> items) =>
        new(items.Select(item => new SupportPublicGeoItemDto(item.Id, item.Name)).ToList());
}

/// <summary>Las ciudades de un departamento. Un departamento que no existe es 404 <c>support.public.department_not_found</c>.</summary>
public sealed class ListPublicSupportCitiesHandler(SupportPublicResolver resolver, ISupportGeography geography)
    : IQueryHandler<ListPublicSupportCitiesQuery, SupportPublicGeoListDto>
{
    public async Task<SupportPublicGeoListDto> HandleAsync(ListPublicSupportCitiesQuery query, CancellationToken cancellationToken)
    {
        _ = await resolver.ResolveAsync(query.TenantSlug, cancellationToken);
        var cities = await geography.CitiesAsync(query.DepartmentId, cancellationToken)
            ?? throw new ResourceNotFoundException(SupportErrorCodes.PublicDepartmentNotFound, "The department was not found.");
        return ListPublicSupportDepartmentsHandler.ToDto(cities);
    }
}
