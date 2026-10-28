using BuildingBlocks.Application;
using FluentValidation;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

// Spec 2026-10-26 §6: las lecturas de la consola de ciudades. En cada handler la autorización de operador (Tenancy) va primero,
// antes de validar o tocar un repositorio: un 403 no depende ni de la entrada ni de si el id existe.

public sealed record GetCitySummaryQuery(TenantId TenantId) : IQuery<CitySummaryDto>;

public sealed record ListUnresolvedCitiesQuery(
    TenantId TenantId, string? Status, Guid? DepartmentId, string? Search, int Page, int PageSize) : IQuery<UnresolvedCityPageDto>;

public sealed record GetUnresolvedCityQuery(TenantId TenantId, UnresolvedCityId Id) : IQuery<UnresolvedCityDetailDto>;

/// <summary>Un valor fuera de rango o un estado que no es <c>Pending</c> ni <c>Discarded</c> es <c>422 validation.failed</c>, con
/// la clave del campo (<c>Status</c>, <c>Page</c>, <c>PageSize</c>, <c>Search</c>), nunca 500.</summary>
public sealed class ListUnresolvedCitiesValidator : AbstractValidator<ListUnresolvedCitiesQuery>
{
    public ListUnresolvedCitiesValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => status is null || CityOperatorInput.IsListStatus(status))
            .WithMessage("The status must be Pending or Discarded.");
        RuleFor(query => query.Search).MaximumLength(CityOperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).InclusiveBetween(1, CityOperatorInput.MaxPage);
        RuleFor(query => query.PageSize).InclusiveBetween(1, CityOperatorInput.MaxPageSize);
    }
}

public sealed class GetCitySummaryHandler(
    IUnresolvedCityRepository unresolvedCities,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetCitySummaryQuery, CitySummaryDto>
{
    public async Task<CitySummaryDto> HandleAsync(GetCitySummaryQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);

        var counts = await unresolvedCities.CountAsync(cancellationToken);
        return new CitySummaryDto(counts.Pending, counts.Discarded, counts.Synonyms, counts.OrdersWaiting);
    }
}

public sealed class ListUnresolvedCitiesHandler(
    IUnresolvedCityRepository unresolvedCities,
    IDepartmentRepository departments,
    ICityRepository cities,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListUnresolvedCitiesQuery> validator)
    : IQueryHandler<ListUnresolvedCitiesQuery, UnresolvedCityPageDto>
{
    public async Task<UnresolvedCityPageDto> HandleAsync(ListUnresolvedCitiesQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : CityText.Normalize(query.Search);
        var (rows, total) = await unresolvedCities.ListAsync(
            CityOperatorInput.ParseListStatus(query.Status),
            query.DepartmentId is { } departmentId ? new DepartmentId(departmentId) : null,
            search,
            query.Page,
            query.PageSize,
            cancellationToken);

        // Una consulta por cosa y no por fila: los departamentos de la página y todas sus ciudades, para las sugerencias.
        var departmentIds = rows.Select(row => row.City.DepartmentId).Distinct().ToList();
        var departmentsById = (await departments.ListByIdsAsync(departmentIds, cancellationToken))
            .ToDictionary(department => department.Id);
        var citiesByDepartment = (await cities.ListByDepartmentsAsync(departmentIds, cancellationToken))
            .GroupBy(city => city.DepartmentId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<City>)group.ToList());

        var items = rows
            .Select(row => UnresolvedCityViews.ListItem(
                row,
                departmentsById[row.City.DepartmentId],
                citiesByDepartment.GetValueOrDefault(row.City.DepartmentId) ?? []))
            .ToList();
        // La página pedida, no un recorte: la pantalla distingue «vacío» de «te pasaste de página» con ella.
        return new UnresolvedCityPageDto(items, total, query.Page, query.PageSize);
    }
}

public sealed class GetUnresolvedCityHandler(
    IUnresolvedCityRepository unresolvedCities,
    IDepartmentRepository departments,
    ICityRepository cities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetUnresolvedCityQuery, UnresolvedCityDetailDto>
{
    public async Task<UnresolvedCityDetailDto> HandleAsync(GetUnresolvedCityQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);

        // Resolved y Discarded también responden, con su estado: sólo un id que nunca existió es 404.
        var row = await unresolvedCities.FindAsync(query.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("geography.unresolved.not_found", "The unresolved city was not found.");
        var department = await departments.FindAsync(row.DepartmentId, cancellationToken)
            ?? throw new InvalidOperationException($"The department '{row.DepartmentId}' of unresolved city '{row.Id}' does not exist.");
        var departmentCities = await cities.ListByDepartmentAsync(row.DepartmentId, cancellationToken);
        var occurrences = await unresolvedCities.ListOccurrencesAsync(row.Id, cancellationToken);

        var byTenant = occurrences
            .GroupBy(occurrence => occurrence.TenantId)
            .Select(group => (TenantId: group.Key, Count: group.Count()))
            .ToList();
        var names = await insights.ReadTenantNamesAsync(byTenant.Select(tenant => tenant.TenantId).ToList(), cancellationToken);
        var tenants = byTenant
            .Select(tenant => new UnresolvedCityTenantDto(tenant.TenantId, UnresolvedCityViews.TenantName(names, tenant.TenantId), tenant.Count))
            .OrderByDescending(tenant => tenant.OrderCount)
            .ThenBy(tenant => tenant.Name, StringComparer.Ordinal)
            .ToList();

        return new UnresolvedCityDetailDto(
            row.Id.Value,
            row.CityText,
            row.NormalizedText,
            UnresolvedCityViews.Department(department),
            row.Status.ToString(),
            row.SampleAddress,
            row.ProvinceCode,
            occurrences.Count,
            tenants,
            row.FirstSeenAt,
            row.LastSeenAt,
            UnresolvedCityViews.Suggestions(row, departmentCities),
            row.SynonymId?.Value,
            row.Version);
    }
}
