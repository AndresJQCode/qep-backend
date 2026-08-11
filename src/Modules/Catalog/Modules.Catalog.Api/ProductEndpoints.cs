using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/catalog")
            .WithTags("Catalog");

        group.MapGet("/products", ListProductsAsync)
            .RequireAuthorization(CatalogPermissions.ProductRead)
            .Produces<ProductsResponse>();

        return endpoints;
    }

    private static async Task<IResult> ListProductsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null)
    {
        var products = await dispatcher.QueryAsync(
            new ListProductsQuery(tenantId, search),
            cancellationToken);

        return Results.Ok(new ProductsResponse(
            products.Select(ToResponse).ToArray()));
    }

    private static ProductResponse ToResponse(ProductDto product) => new(
        product.Id,
        product.Name,
        product.Code,
        product.IsActive,
        product.CreatedAt,
        product.UpdatedAt);
}
