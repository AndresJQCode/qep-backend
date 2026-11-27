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

        group.MapPost("/unresolved/{id:guid}/resolve", ResolveAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Accepts<BillingCityChoiceRequest>("application/json")
            .Produces<ForeignCitySynonymDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/unresolved/{id:guid}/discard", DiscardAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Produces<UnresolvedForeignCityListItemDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/unresolved/{id:guid}/restore", RestoreAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Produces<UnresolvedForeignCityListItemDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/synonyms/{id:guid}", CorrectSynonymAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Accepts<CorrectForeignCitySynonymRequest>("application/json")
            .Produces<ForeignCitySynonymDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/synonyms/{id:guid}", DeleteSynonymAsync)
            .RequireAuthorization(GeographyOperatorPermissions.CitiesManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

    // Un billingCityId ausente o nulo llega como Guid.Empty y es 422 del validador (BillingCityId), no 400 del binder.
    private static async Task<IResult> ResolveAsync(
        Guid tenantId, Guid id, BillingCityChoiceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResolveUnresolvedForeignCityCommand(
                new TenantId(tenantId), new UnresolvedForeignCityId(id), request.BillingCityId ?? Guid.Empty),
            cancellationToken));

    private static async Task<IResult> DiscardAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DiscardUnresolvedForeignCityCommand(new TenantId(tenantId), new UnresolvedForeignCityId(id)), cancellationToken));

    private static async Task<IResult> RestoreAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RestoreUnresolvedForeignCityCommand(new TenantId(tenantId), new UnresolvedForeignCityId(id)), cancellationToken));

    // Un billingCityId o una expectedVersion ausentes llegan como 0 y son 422 del validador, no 400 del binder.
    private static async Task<IResult> CorrectSynonymAsync(
        Guid tenantId, Guid id, CorrectForeignCitySynonymRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new CorrectForeignCitySynonymCommand(
                new TenantId(tenantId), new ForeignCitySynonymId(id), request.BillingCityId ?? Guid.Empty, request.ExpectedVersion ?? 0L),
            cancellationToken));

    private static async Task<IResult> DeleteSynonymAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteForeignCitySynonymCommand(new TenantId(tenantId), new ForeignCitySynonymId(id)), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>La ciudad de la lista de Siigo elegida en el modal de confirmar. Nullable para que un campo ausente o nulo sea 422 del
/// validador (<c>BillingCityId</c>), no un 400 del binder.</summary>
public sealed record BillingCityChoiceRequest(Guid? BillingCityId);

/// <summary>La ciudad nueva del sinónimo y la versión que la pantalla tenía. Nullable para que un campo ausente o nulo sea 422 del
/// validador (<c>BillingCityId</c>, <c>ExpectedVersion</c>), no un 400 del binder.</summary>
public sealed record CorrectForeignCitySynonymRequest(Guid? BillingCityId, long? ExpectedVersion);
