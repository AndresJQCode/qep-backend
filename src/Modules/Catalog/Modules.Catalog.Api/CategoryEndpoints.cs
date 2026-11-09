using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

// Las categorías de producto (spec 2026-10-26 §3.2) y su administración con el acceso rápido de la
// caja (spec 2026-10-26-categorias-pos-acceso-rapido). Sin permisos propios: leer es
// catalog.product.read y toda escritura catalog.product.manage, cuyas políticas ya existen.
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

        // Lista entera, sin If-Match: gana la última escritura. Ruta literal y {categoryId:guid} abajo:
        // la restricción de guid es lo que impide que «pos-quick-access» se lea como un id.
        group.MapPut("/categories/pos-quick-access", ReplacePosQuickAccessAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<ReplacePosQuickAccessRequest>("application/json")
            .Produces<CategoriesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Renombrar y/o mover. If-Match con la versión cargada: 428 sin él, 412 si es vieja.
        group.MapPut("/categories/{categoryId:guid}", UpdateCategoryAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<UpdateCategoryRequest>("application/json")
            .Produces<CategoryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // 422 has_products / has_children: como el DELETE de una tasa, «en uso» es una regla de negocio.
        group.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

    private static async Task<IResult> UpdateCategoryAsync(
        Guid tenantId,
        Guid categoryId,
        UpdateCategoryRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var category = await dispatcher.SendAsync(
            new UpdateCategoryCommand(
                tenantId, categoryId, request.Name, request.ParentId, CatalogIfMatch.RequireVersion(httpContext, "category")),
            cancellationToken);

        return Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> DeleteCategoryAsync(
        Guid tenantId,
        Guid categoryId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteCategoryCommand(tenantId, categoryId, CatalogIfMatch.RequireVersion(httpContext, "category")),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ReplacePosQuickAccessAsync(
        Guid tenantId,
        ReplacePosQuickAccessRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var categories = await dispatcher.SendAsync(
            new ReplacePosQuickAccessCommand(tenantId, request.CategoryIds),
            cancellationToken);

        return Results.Ok(new CategoriesResponse(categories.Select(ToResponse).ToArray()));
    }

    private static CategoryResponse ToResponse(CategoryDto category) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.ProductCount,
        category.Version,
        category.CreatedAt,
        category.UpdatedAt,
        category.PosQuickAccessPosition);
}
