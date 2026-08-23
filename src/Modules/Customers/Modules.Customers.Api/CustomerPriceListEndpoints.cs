using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Customers.Application;

namespace Modules.Customers.Api;

// Archivo propio y no un bloque mas de CustomerEndpoints: son dos recursos distintos, mismo
// criterio que ClientClassificationEndpoints frente a CustomerEndpoints.
public static class CustomerPriceListEndpoints
{
    public static IEndpointRouteBuilder MapCustomerPriceListEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/customers/{customerId:guid}/price-lists")
            .WithTags("Customers");

        group.MapGet("/", ListCustomerPriceListsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<CustomerPriceListsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // PUT y no POST/PATCH: reemplaza el conjunto entero de listas asignadas, mismo criterio
        // que el PUT de producto con sus escalas. Evita duplicados por construccion — el body es
        // el conjunto final, no un delta.
        group.MapPut("/", SetCustomerPriceListsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<SetCustomerPriceListsRequest>("application/json")
            .Produces<CustomerPriceListsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListCustomerPriceListsAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceLists = await dispatcher.QueryAsync(
            new ListCustomerPriceListsQuery(tenantId, customerId), cancellationToken);

        return Results.Ok(new CustomerPriceListsResponse(priceLists));
    }

    private static async Task<IResult> SetCustomerPriceListsAsync(
        Guid tenantId,
        Guid customerId,
        SetCustomerPriceListsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var priceLists = await dispatcher.SendAsync(
            new SetCustomerPriceListsCommand(tenantId, customerId, request.PriceListIds),
            cancellationToken);

        return Results.Ok(new CustomerPriceListsResponse(priceLists));
    }
}
