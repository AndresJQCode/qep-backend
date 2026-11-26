using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Api;

/// <summary>
/// Endpoints de datos de referencia DIVIPOLA. A diferencia del resto de los módulos, Geography no
/// tiene tenant ni permiso propio: son datos globales, y basta con <c>RequireAuthorization()</c>
/// a secas — cualquier usuario autenticado puede leerlos. Mismo patrón que
/// <c>GET /api/v1/auth/me</c> en <c>AuthSessionEndpoints</c>.
/// </summary>
public static class GeographyEndpoints
{
    public static IEndpointRouteBuilder MapGeographyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/departments", ListDepartmentsAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<DepartmentResponse>>();

        // Spec 2026-11-20 (RRHH fase 1, D-10): el catálogo de países (ISO 3166-1 alfa-2), global como los departamentos.
        endpoints.MapGet("/api/v1/countries", ListCountriesAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<CountryResponse>>();

        endpoints.MapGet("/api/v1/cities", ListCitiesAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<CityResponse>>();

        endpoints.MapGet("/api/v1/countries/{country}/regions", ListRegionsAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<RegionResponse>>();

        endpoints.MapGet("/api/v1/countries/{country}/regions/{regionId:guid}/cities", ListRegionCitiesAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<RegionCityResponse>>();

        return endpoints;
    }

    private static async Task<IResult> ListRegionsAsync(
        string country, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var regions = await dispatcher.QueryAsync(new ListRegionsQuery(country), cancellationToken);
        return Results.Ok(regions
            .Select(region => new RegionResponse(region.Id, region.Code, region.Name))
            .ToArray());
    }

    private static async Task<IResult> ListRegionCitiesAsync(
        string country, Guid regionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var cities = await dispatcher.QueryAsync(
            new ListRegionCitiesQuery(country, regionId), cancellationToken);
        return Results.Ok(cities
            .Select(city => new RegionCityResponse(city.Id, city.Code, city.Name))
            .ToArray());
    }

    private static async Task<IResult> ListDepartmentsAsync(
        IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var departments = await dispatcher.QueryAsync(new ListDepartmentsQuery(), cancellationToken);
        return Results.Ok(departments
            .Select(department => new DepartmentResponse(
                department.Id, department.DivipolaCode, department.Name))
            .ToArray());
    }

    private static async Task<IResult> ListCountriesAsync(
        IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var countries = await dispatcher.QueryAsync(new ListCountriesQuery(), cancellationToken);
        return Results.Ok(countries
            .Select(country => new CountryResponse(country.Code, country.Name, country.RegionKind))
            .ToArray());
    }

    private static async Task<IResult> ListCitiesAsync(
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? departmentId = null)
    {
        // El binding automático de un Guid requerido dispara BadHttpRequestException cuando el
        // parámetro falta, pero ApiExceptionHandler intercepta *toda* excepción no reconocida y
        // la reduce a 500 — no deja pasar el 400 automático de ASP.NET Core. Por eso el parámetro
        // es nullable y la validación es manual acá, en vez de confiar en el binding.
        if (departmentId is not { } value)
        {
            return Results.BadRequest();
        }

        var cities = await dispatcher.QueryAsync(
            new ListCitiesQuery(new DepartmentId(value)), cancellationToken);
        return Results.Ok(cities
            .Select(city => new CityResponse(
                city.Id, city.DivipolaCode, city.Name, city.DepartmentId))
            .ToArray());
    }
}

/// <summary><c>RegionKind</c> es null si el país no tiene catálogo de regiones: la pantalla cae al texto libre.</summary>
public sealed record CountryResponse(string Code, string Name, string? RegionKind);

public sealed record DepartmentResponse(Guid Id, string DivipolaCode, string Name);

public sealed record CityResponse(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

public sealed record RegionResponse(Guid Id, string Code, string Name);

public sealed record RegionCityResponse(Guid Id, string Code, string Name);
