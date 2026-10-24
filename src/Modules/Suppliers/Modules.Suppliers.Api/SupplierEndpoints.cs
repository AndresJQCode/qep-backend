using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Suppliers.Application;

namespace Modules.Suppliers.Api;

public static class SupplierEndpoints
{
    public static IEndpointRouteBuilder MapSupplierEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como companies y customers: la política frena a quien no tiene el
        // permiso y el handler (SuppliersAuthorization) a quien lo tiene en otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/suppliers")
            .WithTags("Suppliers");

        group.MapGet("/", ListSuppliersAsync)
            .RequireAuthorization(SuppliersPermissions.Read)
            .Produces<SuppliersResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Antes que /{supplierId:guid}: las dos rutas no chocan (la segunda exige Guid), pero así
        // se lee en el orden en que el frontend las usa.
        group.MapGet("/categories", ListSupplierCategoriesAsync)
            .RequireAuthorization(SuppliersPermissions.Read)
            .Produces<SupplierCategoriesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{supplierId:guid}", GetSupplierAsync)
            .RequireAuthorization(SuppliersPermissions.Read)
            .Produces<SupplierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateSupplierAsync)
            .RequireAuthorization(SuppliersPermissions.Manage)
            .Accepts<SupplierWriteRequest>("application/json")
            .Produces<SupplierResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{supplierId:guid}", UpdateSupplierAsync)
            .RequireAuthorization(SuppliersPermissions.Manage)
            .Accepts<SupplierWriteRequest>("application/json")
            .Produces<SupplierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{supplierId:guid}/deactivate", DeactivateSupplierAsync)
            .RequireAuthorization(SuppliersPermissions.Manage)
            .Produces<SupplierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{supplierId:guid}/activate", ActivateSupplierAsync)
            .RequireAuthorization(SuppliersPermissions.Manage)
            .Produces<SupplierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 202 y no 200 con el archivo: se sube al almacenamiento durante el request y el enlace llega
        // por correo, así que lo que se acepta aquí es la solicitud, no la descarga. Mismo permiso que
        // la lista: exportar es leer lo que la grilla ya muestra.
        group.MapPost("/export", ExportSuppliersAsync)
            .RequireAuthorization(SuppliersPermissions.Read)
            .Produces<ExportSuppliersResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    // status llega como texto: un valor fuera de active/inactive es un 422 con su código, no un 400
    // del binding. pageSize toma el default del tope del módulo; el handler lo normaliza.
    private static async Task<IResult> ListSuppliersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? category = null,
        int page = 1,
        int pageSize = SupplierPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListSuppliersQuery(tenantId, search, status, category, page, pageSize),
            cancellationToken);

        return Results.Ok(new SuppliersResponse(
            result.Items, result.Total, result.Page, result.PageSize));
    }

    // Los filtros son los de la lista y viajan por query, no en el cuerpo: es un POST porque tiene
    // efecto (sube un archivo, encola un correo), no porque lleve datos.
    private static async Task<IResult> ExportSuppliersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? category = null)
    {
        var result = await dispatcher.SendAsync(
            new ExportSuppliersCommand(tenantId, search, status, category), cancellationToken);

        return Results.Accepted(value: new ExportSuppliersResponse(
            result.FileName, result.SupplierCount, result.ExpiresAt));
    }

    private static async Task<IResult> ListSupplierCategoriesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var categories = await dispatcher.QueryAsync(
            new ListSupplierCategoriesQuery(tenantId), cancellationToken);

        return Results.Ok(new SupplierCategoriesResponse(categories));
    }

    private static async Task<IResult> GetSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var supplier = await dispatcher.QueryAsync(
            new GetSupplierQuery(tenantId, supplierId), cancellationToken);

        return Results.Ok(supplier);
    }

    private static async Task<IResult> CreateSupplierAsync(
        Guid tenantId,
        SupplierWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var supplier = await dispatcher.SendAsync(
            CreateSupplierCommand.From(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/suppliers/{supplier.Id}", supplier);
    }

    private static async Task<IResult> UpdateSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        SupplierWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var supplier = await dispatcher.SendAsync(
            UpdateSupplierCommand.From(tenantId, supplierId, request), cancellationToken);

        return Results.Ok(supplier);
    }

    private static async Task<IResult> DeactivateSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var supplier = await dispatcher.SendAsync(
            new DeactivateSupplierCommand(tenantId, supplierId), cancellationToken);

        return Results.Ok(supplier);
    }

    private static async Task<IResult> ActivateSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var supplier = await dispatcher.SendAsync(
            new ActivateSupplierCommand(tenantId, supplierId), cancellationToken);

        return Results.Ok(supplier);
    }
}
