using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

// Variantes, Tarea 4: la biblioteca de opciones del tenant (spec 2026-10-20 §3.1). Bajo el mismo
// prefijo que productos y tasas; sin permisos propios: leer es catalog.product.read y administrar
// catalog.product.manage, cuyas políticas ya existen.
public static class OptionEndpoints
{
    public static IEndpointRouteBuilder MapCatalogOptionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/catalog")
            .WithTags("Catalog");

        // El primer listado de un tenant siembra «Talla» y «Color» inactivas.
        group.MapGet("/options", ListOptionsAsync)
            .RequireAuthorization(CatalogPermissions.ProductRead)
            .Produces<OptionsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/options", CreateOptionAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<CreateOptionRequest>("application/json")
            .Produces<OptionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 409 catalog.option.in_use: el cuerpo deja afuera un valor que alguna variante usa.
        group.MapPut("/options/{optionId:guid}", UpdateOptionAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Accepts<UpdateOptionRequest>("application/json")
            .Produces<OptionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 409 catalog.option.in_use si alguna variante usa la opción o un valor: se desactiva con
        // el PUT en vez de borrarse.
        group.MapDelete("/options/{optionId:guid}", DeleteOptionAsync)
            .RequireAuthorization(CatalogPermissions.ProductManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListOptionsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListOptionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateOptionAsync(
        Guid tenantId,
        CreateOptionRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // values es opcional al crear: una opción puede nacer sin valores y llenarse después.
        var option = await dispatcher.SendAsync(
            new CreateOptionCommand(tenantId, request.Name, request.Values ?? []),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/catalog/options/{option.Id}",
            option);
    }

    private static async Task<IResult> UpdateOptionAsync(
        Guid tenantId,
        Guid optionId,
        UpdateOptionRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // Sin `?? []`: el validador rechaza values ausente, porque el PUT reemplaza la lista y
        // un cuerpo sin ella borraría todos los valores.
        var option = await dispatcher.SendAsync(
            new UpdateOptionCommand(tenantId, optionId, request.Name, request.IsActive, request.Values!),
            cancellationToken);

        return Results.Ok(option);
    }

    private static async Task<IResult> DeleteOptionAsync(
        Guid tenantId,
        Guid optionId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteOptionCommand(tenantId, optionId), cancellationToken);

        return Results.NoContent();
    }
}
