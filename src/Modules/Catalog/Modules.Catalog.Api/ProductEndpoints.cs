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

        // POST y no GET aunque no lleve cuerpo: tiene efecto (sube un archivo, manda un
        // correo), asi que no es cacheable ni repetible sin consecuencias. Los filtros viajan
        // por query string igual que en el listado.
        group.MapPost("/products/export", ExportProductsAsync)
            .RequireAuthorization(CatalogPermissions.ProductRead)
            .Produces<ProductExportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

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

        // La vuelta de deactivate (CAT-07). Verbo dedicado y no un isActive editable en el PUT,
        // que es la decisión que CAT-02b ya tomó para el camino de ida: un booleano en el PUT
        // dejaría el cambio de estado sin evento de auditoría propio y sin invariante que lo
        // custodie. Sin permiso nuevo — activar es administrar.
        group.MapPost("/products/{productId:guid}/activate", ActivateProductAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ExportProductsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? name = null,
        string? code = null,
        bool? isActive = null)
    {
        var result = await dispatcher.SendAsync(
            new ExportProductsCommand(tenantId, name, code, isActive), cancellationToken);

        // 202 y no 200: lo que se acepto es la exportacion. El archivo se sube durante el
        // request pero el correo lo manda el worker despues, asi que la respuesta no trae el
        // resultado final.
        return Results.Accepted(value: new ProductExportResponse(
            result.FileName, result.ProductCount, result.ExpiresAt));
    }

    private static async Task<IResult> ListProductsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? name = null,
        string? code = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = ProductPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListProductsQuery(tenantId, search, name, code, isActive, page, pageSize),
            cancellationToken);

        return Results.Ok(new ProductsResponse(
            result.Items.Select(ToResponse).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
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
                request.TaxRateId,
                request.Pricing),
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
                request.TaxRateId,
                request.Pricing),
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

    private static async Task<IResult> ActivateProductAsync(
        Guid tenantId,
        Guid productId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var product = await dispatcher.SendAsync(
            new ActivateProductCommand(tenantId, productId),
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
        product.TaxRateId,
        product.PriceBaseUsd,
        product.PriceBaseCop,
        product.PriceScales,
        product.CreatedAt,
        product.UpdatedAt);
}
