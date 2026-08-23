using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pricing.Application;

namespace Modules.Pricing.Api;

public static class PriceListEndpoints
{
    public static IEndpointRouteBuilder MapPriceListEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/pricing/price-lists")
            .WithTags("Pricing");

        group.MapGet("/", ListPriceListsAsync)
            .RequireAuthorization(PricingPermissions.PriceListRead)
            .Produces<PriceListsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{priceListId:guid}", GetPriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListRead)
            .Produces<PriceListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreatePriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListManage)
            .Accepts<CreatePriceListRequest>("application/json")
            .Produces<PriceListResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{priceListId:guid}", UpdatePriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListManage)
            .Accepts<UpdatePriceListRequest>("application/json")
            .Produces<PriceListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{priceListId:guid}/activate", ActivatePriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListManage)
            .Produces<PriceListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{priceListId:guid}/deactivate", DeactivatePriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListManage)
            .Produces<PriceListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{priceListId:guid}", DeletePriceListAsync)
            .RequireAuthorization(PricingPermissions.PriceListManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListPriceListsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceLists = await dispatcher.QueryAsync(
            new ListPriceListsQuery(tenantId), cancellationToken);

        return Results.Ok(new PriceListsResponse(priceLists.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> GetPriceListAsync(
        Guid tenantId,
        Guid priceListId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceList = await dispatcher.QueryAsync(
            new GetPriceListQuery(tenantId, priceListId), cancellationToken);

        return Results.Ok(ToResponse(priceList));
    }

    private static async Task<IResult> CreatePriceListAsync(
        Guid tenantId,
        CreatePriceListRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceList = await dispatcher.SendAsync(
            new CreatePriceListCommand(tenantId, request.Name, request.Prefix), cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/pricing/price-lists/{priceList.Id}",
            ToResponse(priceList));
    }

    private static async Task<IResult> UpdatePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        UpdatePriceListRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceList = await dispatcher.SendAsync(
            new UpdatePriceListCommand(tenantId, priceListId, request.Name, request.Prefix),
            cancellationToken);

        return Results.Ok(ToResponse(priceList));
    }

    private static async Task<IResult> ActivatePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceList = await dispatcher.SendAsync(
            new ActivatePriceListCommand(tenantId, priceListId), cancellationToken);

        return Results.Ok(ToResponse(priceList));
    }

    private static async Task<IResult> DeactivatePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceList = await dispatcher.SendAsync(
            new DeactivatePriceListCommand(tenantId, priceListId), cancellationToken);

        return Results.Ok(ToResponse(priceList));
    }

    private static async Task<IResult> DeletePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeletePriceListCommand(tenantId, priceListId), cancellationToken);

        return Results.NoContent();
    }

    private static PriceListResponse ToResponse(PriceListDto priceList) =>
        new(
            priceList.Id,
            priceList.Name,
            priceList.Prefix,
            priceList.IsActive,
            priceList.CreatedAt,
            priceList.UpdatedAt);
}
