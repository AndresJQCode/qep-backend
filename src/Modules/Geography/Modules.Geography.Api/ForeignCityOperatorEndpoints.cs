using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Api;

/// <summary>
/// Spec 2026-11-26 «Consola del operador»: la consola de ciudades del exterior, bajo <c>/operator/cities/foreign</c>. Mismos
/// permisos que la colombiana (<c>operator.cities.read</c> y <c>.manage</c>) y la misma doble capa: la política exige el permiso
/// y el handler revalida con <c>OperatorAuthorization</c>.
/// </summary>
public static class ForeignCityOperatorEndpoints
{
    public static IEndpointRouteBuilder MapForeignCityOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator/cities/foreign")
            .WithTags("Operator");

        group.MapGet("/countries", ListCountriesAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<IReadOnlyList<BillingCountryDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/catalog", SearchCatalogAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<IReadOnlyList<BillingCityRefDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/unresolved", ListUnresolvedAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<UnresolvedForeignCityPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/unresolved/{id:guid}", GetUnresolvedAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<UnresolvedForeignCityDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/synonyms", ListSynonymsAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesRead)
            .Produces<ForeignCitySynonymPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListCountriesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListBillingCountriesQuery(new TenantId(tenantId)), cancellationToken));

    private static async Task<IResult> SearchCatalogAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? country = null,
        string? search = null) =>
        Results.Ok(await dispatcher.QueryAsync(
            new SearchBillingCitiesQuery(new TenantId(tenantId), country, search), cancellationToken));

    private static async Task<IResult> ListUnresolvedAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? country = null,
        string? status = null,
        string? search = null,
        int page = 1,
        int pageSize = CityOperatorInput.DefaultPageSize) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListUnresolvedForeignCitiesQuery(new TenantId(tenantId), country, status, search, page, pageSize),
            cancellationToken));

    private static async Task<IResult> GetUnresolvedAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new GetUnresolvedForeignCityQuery(new TenantId(tenantId), new UnresolvedForeignCityId(id)), cancellationToken));

    private static async Task<IResult> ListSynonymsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? country = null,
        string? search = null,
        int page = 1,
        int pageSize = CityOperatorInput.DefaultPageSize) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListForeignCitySynonymsQuery(new TenantId(tenantId), country, search, page, pageSize), cancellationToken));
}
