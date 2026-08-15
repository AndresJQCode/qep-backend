using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            .Produces<ProductsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/products/{productId:guid}", GetProductAsync)
            .RequireAuthorization(CatalogPermissions.ProductRead)
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/products", CreateProductAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<CreateProductRequest>("application/json")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/products/{productId:guid}", UpdateProductAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<UpdateProductRequest>("application/json")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/products/{productId:guid}/deactivate", DeactivateProductAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

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

        return Results.Ok(new ProductsResponse(products.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> GetProductAsync(
        Guid tenantId,
        Guid productId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var product = await dispatcher.QueryAsync(
            new GetProductQuery(tenantId, productId),
            cancellationToken);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> CreateProductAsync(
        Guid tenantId,
        CreateProductRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var product = await dispatcher.SendAsync(
            new CreateProductCommand(
                tenantId,
                request.Name,
                request.Code,
                request.Description,
                request.ImageFileId,
                request.Price,
                request.Currency,
                request.TaxRateId),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/catalog/products/{product.Id}",
            ToResponse(product));
    }

    private static async Task<IResult> UpdateProductAsync(
        Guid tenantId,
        Guid productId,
        UpdateProductRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var product = await dispatcher.SendAsync(
            new UpdateProductCommand(
                tenantId,
                productId,
                request.Name,
                request.Code,
                request.Description,
                request.ImageFileId,
                request.Price,
                request.Currency,
                request.TaxRateId),
            cancellationToken);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> DeactivateProductAsync(
        Guid tenantId,
        Guid productId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var product = await dispatcher.SendAsync(
            new DeactivateProductCommand(tenantId, productId),
            cancellationToken);

        return Results.Ok(ToResponse(product));
    }

    private static ProductResponse ToResponse(ProductDto product) => new(
        product.Id,
        product.Name,
        product.Code,
        product.IsActive,
        product.Description,
        product.ImageFileId,
        product.ImageUrl,
        product.Price,
        product.Currency,
        product.TaxRateId,
        product.CreatedAt,
        product.UpdatedAt);
}
