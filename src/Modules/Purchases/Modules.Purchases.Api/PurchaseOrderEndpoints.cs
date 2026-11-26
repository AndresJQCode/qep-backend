using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Api;

public static class PurchaseOrderEndpoints
{
    private const string Resource = "purchase order";

    public static IEndpointRouteBuilder MapPurchaseOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como suppliers y loans: la política frena a quien no tiene el permiso (y, con el módulo apagado,
        // ModuleEntitlementMask ya se lo quitó) y el handler (PurchasesAuthorization) a quien lo tiene en otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/purchases/orders")
            .WithTags("Purchases");

        group.MapGet("/", ListOrdersAsync)
            .RequireAuthorization(PurchasesPermissions.OrderRead)
            .Produces<PurchaseOrdersResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", CreateOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Accepts<PurchaseOrderWriteRequest>("application/json")
            .Produces<PurchaseOrderDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Ruta literal antes de /{orderId:guid}. Es un POST que no escribe: lleva el cuerpo del alta y el permiso de gestionar.
        group.MapPost("/preview", PreviewOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Accepts<PurchaseOrderWriteRequest>("application/json")
            .Produces<PurchaseOrderPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{orderId:guid}", GetOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderRead)
            .Produces<PurchaseOrderDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // En cualquier estado y con el permiso de leer. 502 si qcode-pdf no lo devuelve.
        group.MapGet("/{orderId:guid}/pdf", GetOrderPdfAsync)
            .RequireAuthorization(PurchasesPermissions.OrderRead)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPut("/{orderId:guid}", UpdateOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Accepts<PurchaseOrderWriteRequest>("application/json")
            .Produces<PurchaseOrderDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{orderId:guid}", DeleteOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        MapTransition(group, "/{orderId:guid}/send", PurchaseOrderAction.Send);

        group.MapPost("/{orderId:guid}/confirm", ConfirmOrderAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Accepts<ConfirmPurchaseOrderRequest>("application/json")
            .Produces<PurchaseOrderDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Reenviar el correo al proveedor: no cambia la orden, así que va sin If-Match y sin subir la versión. Un fallo del canal es
        // 200 con email.status = Failed; sin correo del proveedor, 422.
        group.MapPost("/{orderId:guid}/resend-email", ResendEmailAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Produces<PurchaseOrderDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        MapTransition(group, "/{orderId:guid}/receive", PurchaseOrderAction.Receive);
        MapTransition(group, "/{orderId:guid}/cancel", PurchaseOrderAction.Cancel);

        // Mayorista fase 3: el catálogo del mayorista de un proveedor vinculado, para armar la orden (por eso exige gestionar). El
        // mayorista sale del proveedor guardado en el tenant de la ruta: el de otro tenant es 404, uno no vinculado 422 y una
        // relación que no está activa 403.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/purchases/suppliers")
            .WithTags("Purchases")
            .MapGet("/{supplierId:guid}/catalog", ListSupplierCatalogAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Produces<SupplierCatalogPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-20: el árbol de categorías que la lista del distribuidor le deja ver, para filtrar ese catálogo.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/purchases/suppliers")
            .WithTags("Purchases")
            .MapGet("/{supplierId:guid}/catalog/categories", ListSupplierCatalogCategoriesAsync)
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Produces<SupplierCatalogCategoriesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListSupplierCatalogAsync(
        Guid tenantId,
        Guid supplierId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 0,
        Guid? categoryId = null)
    {
        var result = await dispatcher.QueryAsync(
            new ListSupplierCatalogQuery(tenantId, supplierId, search, page, pageSize, categoryId), cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ListSupplierCatalogCategoriesAsync(
        Guid tenantId,
        Guid supplierId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListSupplierCatalogCategoriesQuery(tenantId, supplierId), cancellationToken));

    // Enviar, recibir y cancelar no llevan cuerpo y comparten handler; sólo cambia la acción.
    private static void MapTransition(RouteGroupBuilder group, string pattern, PurchaseOrderAction action) =>
        group.MapPost(
                pattern,
                (Guid tenantId, Guid orderId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    TransitionOrderAsync(tenantId, orderId, action, httpContext, dispatcher, cancellationToken))
            .RequireAuthorization(PurchasesPermissions.OrderManage)
            .Produces<PurchaseOrderDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    // status llega como texto: un valor fuera de los cinco estados es un 422 con errors.Status, no un 400 del binding. pageSize
    // toma el default del módulo; el handler lo normaliza y lo topa en 200.
    private static async Task<IResult> ListOrdersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? status = null,
        Guid? supplierId = null,
        string? search = null,
        int page = 1,
        int pageSize = PurchaseOrderPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListPurchaseOrdersQuery(tenantId, status, supplierId, search, page, pageSize), cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.QueryAsync(new GetPurchaseOrderQuery(tenantId, orderId), cancellationToken);

        return Results.Ok(order);
    }

    private static async Task<IResult> GetOrderPdfAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var file = await dispatcher.QueryAsync(new GetPurchaseOrderPdfQuery(tenantId, orderId), cancellationToken);

        return Results.File(file.Content, "application/pdf", file.FileName);
    }

    private static async Task<IResult> CreateOrderAsync(
        Guid tenantId,
        PurchaseOrderWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new CreatePurchaseOrderCommand(tenantId, request.SupplierId, request.Currency, request.Notes, request.Lines),
            cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/purchases/orders/{order.Id}", order);
    }

    private static async Task<IResult> PreviewOrderAsync(
        Guid tenantId,
        PurchaseOrderWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var preview = await dispatcher.SendAsync(
            new PreviewPurchaseOrderCommand(tenantId, request.SupplierId, request.Currency, request.Notes, request.Lines),
            cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> UpdateOrderAsync(
        Guid tenantId,
        Guid orderId,
        PurchaseOrderWriteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = PurchasesIfMatch.RequireVersion(httpContext, Resource);
        var order = await dispatcher.SendAsync(
            new UpdatePurchaseOrderCommand(
                tenantId, orderId, version, request.SupplierId, request.Currency, request.Notes, request.Lines),
            cancellationToken);

        return Results.Ok(order);
    }

    private static async Task<IResult> DeleteOrderAsync(
        Guid tenantId,
        Guid orderId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = PurchasesIfMatch.RequireVersion(httpContext, Resource);
        await dispatcher.SendAsync(new DeletePurchaseOrderCommand(tenantId, orderId, version), cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmOrderAsync(
        Guid tenantId,
        Guid orderId,
        ConfirmPurchaseOrderRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = PurchasesIfMatch.RequireVersion(httpContext, Resource);
        var order = await dispatcher.SendAsync(
            new ConfirmPurchaseOrderCommand(tenantId, orderId, version, request.Lines), cancellationToken);

        return Results.Ok(order);
    }

    private static async Task<IResult> ResendEmailAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ResendPurchaseOrderEmailCommand(tenantId, orderId), cancellationToken));

    private static async Task<IResult> TransitionOrderAsync(
        Guid tenantId,
        Guid orderId,
        PurchaseOrderAction action,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = PurchasesIfMatch.RequireVersion(httpContext, Resource);
        var order = await dispatcher.SendAsync(
            new TransitionPurchaseOrderCommand(tenantId, orderId, version, action), cancellationToken);

        return Results.Ok(order);
    }
}
