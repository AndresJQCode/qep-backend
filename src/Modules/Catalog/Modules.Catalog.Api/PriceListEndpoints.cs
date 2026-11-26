using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

/// <summary>Cuerpo del alta y la edición de una lista. <c>showPrices</c> ausente en el <c>PUT</c> conserva el valor guardado.</summary>
public sealed record PriceListWriteRequest(
    string Name,
    decimal AdjustmentPercent,
    string Visibility,
    IReadOnlyList<Guid>? CategoryIds,
    bool? ShowPrices,
    IReadOnlyList<PriceListExclusionInput>? Exclusions);

public sealed record PriceListOverridesWriteRequest(
    IReadOnlyList<PriceListOverrideSetInput>? Set,
    IReadOnlyList<PriceListOverrideRemoveInput>? Remove);

public sealed record ChannelPricesWriteRequest(IReadOnlyList<ChannelPriceInput>? Channels);

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal). Todo con <c>catalog.price_list.manage</c>; los handlers revalidan tenant y
/// permiso. Las escrituras de una lista llevan <c>If-Match</c> con su versión (428 sin él, 412 si es vieja), también el lote de
/// precios fijos. «Precios por canal» no: gana la última escritura.
/// </summary>
public static class PriceListEndpoints
{
    public static IEndpointRouteBuilder MapCatalogPriceListEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/catalog")
            .WithTags("Catalog")
            .RequireAuthorization(CatalogPermissions.PriceListManage);

        group.MapGet("/price-lists", ListAsync)
            .Produces<PriceListsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/price-lists", CreateAsync)
            .Accepts<PriceListWriteRequest>("application/json")
            .Produces<PriceListDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/price-lists/{priceListId:guid}", GetAsync)
            .Produces<PriceListDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/price-lists/{priceListId:guid}", UpdateAsync)
            .Accepts<PriceListWriteRequest>("application/json")
            .Produces<PriceListDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/price-lists/{priceListId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/price-lists/{priceListId:guid}/overrides", ListOverridesAsync)
            .Produces<PriceListOverridesPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/price-lists/{priceListId:guid}/overrides", UpdateOverridesAsync)
            .Accepts<PriceListOverridesWriteRequest>("application/json")
            .Produces<PriceListOverridesWriteDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/channel-prices", GetChannelPricesAsync)
            .Produces<ChannelPricesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/channel-prices", UpdateChannelPricesAsync)
            .Accepts<ChannelPricesWriteRequest>("application/json")
            .Produces<ChannelPricesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPriceListsQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid priceListId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPriceListQuery(tenantId, priceListId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, PriceListWriteRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var created = await dispatcher.SendAsync(
            new CreatePriceListCommand(
                tenantId, request.Name, request.AdjustmentPercent, request.Visibility, request.CategoryIds ?? [],
                request.ShowPrices, request.Exclusions),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/catalog/price-lists/{created.Id}", created);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid priceListId,
        PriceListWriteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdatePriceListCommand(
                tenantId, priceListId, request.Name, request.AdjustmentPercent, request.Visibility, request.CategoryIds ?? [],
                CatalogIfMatch.RequireVersion(httpContext, "price list"), request.ShowPrices, request.Exclusions),
            cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid priceListId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeletePriceListCommand(tenantId, priceListId, CatalogIfMatch.RequireVersion(httpContext, "price list")),
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListOverridesAsync(
        Guid tenantId,
        Guid priceListId,
        string? search,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListPriceListOverridesQuery(tenantId, priceListId, search, page ?? 1, pageSize ?? 50),
            cancellationToken));

    private static async Task<IResult> UpdateOverridesAsync(
        Guid tenantId,
        Guid priceListId,
        PriceListOverridesWriteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdatePriceListOverridesCommand(
                tenantId, priceListId, request.Set, request.Remove, CatalogIfMatch.RequireVersion(httpContext, "price list")),
            cancellationToken));

    private static async Task<IResult> GetChannelPricesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetChannelPricesQuery(tenantId), cancellationToken));

    private static async Task<IResult> UpdateChannelPricesAsync(
        Guid tenantId, ChannelPricesWriteRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new UpdateChannelPricesCommand(tenantId, request.Channels), cancellationToken));
}
