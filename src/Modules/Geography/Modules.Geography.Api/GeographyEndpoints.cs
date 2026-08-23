using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
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

        endpoints.MapGet("/api/v1/cities", ListCitiesAsync)
            .RequireAuthorization()
            .WithTags("Geography")
            .Produces<IReadOnlyList<CityResponse>>();

        return endpoints;
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

public sealed record DepartmentResponse(Guid Id, string DivipolaCode, string Name);

public sealed record CityResponse(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);
