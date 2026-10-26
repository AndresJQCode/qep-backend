using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Api;

/// <summary>
/// Spec 2026-10-26 §6, «Contrato HTTP para el frontend» del plan: la consola de ciudades del operador. La ruta lleva el tenant
/// desde el que actúa el operador, como la consola de tenants (<c>OperatorEndpoints</c> de Tenancy). Doble capa: la política exige
/// el permiso <c>operator.cities.*</c> y el handler revalida con <c>OperatorAuthorization</c>.
/// </summary>
public static class CityOperatorEndpoints
{
    public static IEndpointRouteBuilder MapCityOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator/cities")
            .WithTags("Operator");

        group.MapGet("/summary", GetSummaryAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<CitySummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/unresolved", ListUnresolvedAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<UnresolvedCityPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/unresolved/{id:guid}", GetUnresolvedAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<UnresolvedCityDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCitySummaryQuery(new TenantId(tenantId)), cancellationToken));

    private static async Task<IResult> ListUnresolvedAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? status = null,
        Guid? departmentId = null,
        string? search = null,
        int page = 1,
        int pageSize = CityOperatorInput.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListUnresolvedCitiesQuery(new TenantId(tenantId), status, departmentId, search, page, pageSize),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUnresolvedAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new GetUnresolvedCityQuery(new TenantId(tenantId), new UnresolvedCityId(id)), cancellationToken));
}
