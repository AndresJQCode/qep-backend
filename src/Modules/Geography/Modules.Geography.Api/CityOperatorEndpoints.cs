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

        group.MapPost("/unresolved/{id:guid}/impact", PreviewResolveAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Accepts<CityChoiceRequest>("application/json")
            .Produces<ResolveImpactDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/unresolved/{id:guid}/resolve", ResolveAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Accepts<CityChoiceRequest>("application/json")
            .Produces<CitySynonymDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/unresolved/{id:guid}/discard", DiscardAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Produces<UnresolvedCityListItemDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/unresolved/{id:guid}/restore", RestoreAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Produces<UnresolvedCityListItemDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

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

    // Un cityId ausente o nulo llega como Guid.Empty y es 422 del validador (CityId), no 400 del binder.
    private static async Task<IResult> PreviewResolveAsync(
        Guid tenantId, Guid id, CityChoiceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new PreviewResolveImpactQuery(new TenantId(tenantId), new UnresolvedCityId(id), request.CityId ?? Guid.Empty),
            cancellationToken));

    private static async Task<IResult> ResolveAsync(
        Guid tenantId, Guid id, CityChoiceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResolveUnresolvedCityCommand(new TenantId(tenantId), new UnresolvedCityId(id), request.CityId ?? Guid.Empty),
            cancellationToken));

    private static async Task<IResult> DiscardAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DiscardUnresolvedCityCommand(new TenantId(tenantId), new UnresolvedCityId(id)), cancellationToken));

    private static async Task<IResult> RestoreAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RestoreUnresolvedCityCommand(new TenantId(tenantId), new UnresolvedCityId(id)), cancellationToken));
}

/// <summary>La ciudad elegida en el modal de confirmar. Nullable para que un campo ausente o nulo sea 422 del validador
/// (<c>CityId</c>), no un 400 del binder.</summary>
public sealed record CityChoiceRequest(Guid? CityId);
