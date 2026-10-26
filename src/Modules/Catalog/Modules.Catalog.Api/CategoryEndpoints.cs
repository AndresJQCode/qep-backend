using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

// Las categorías de producto (spec 2026-10-26 §3.2). Sin permisos propios: leer es
// catalog.product.read y crear catalog.product.manage, cuyas políticas ya existen.
public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCatalogCategoryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/catalog")
            .WithTags("Catalog");

        group.MapGet("/categories", ListCategoriesAsync)
            .RequireAuthorization(CatalogPermissions.ProductRead)
            .Produces<CategoriesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/categories", CreateCategoryAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<CreateCategoryRequest>("application/json")
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListCategoriesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var categories = await dispatcher.QueryAsync(
            new ListCategoriesQuery(tenantId),
            cancellationToken);

        return Results.Ok(new CategoriesResponse(categories.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> CreateCategoryAsync(
        Guid tenantId,
        CreateCategoryRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var category = await dispatcher.SendAsync(
            new CreateCategoryCommand(tenantId, request.Name, request.ParentId),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/catalog/categories/{category.Id}",
            ToResponse(category));
    }

    private static CategoryResponse ToResponse(CategoryDto category) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.ProductCount,
        category.Version,
        category.CreatedAt,
        category.UpdatedAt);
}
