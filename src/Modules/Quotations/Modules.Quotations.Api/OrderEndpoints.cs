using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;

namespace Modules.Quotations.Api;

// TEMPORAL (a pedido, 2026-08-24): mismo interruptor que QuotationEndpoints -- las políticas por
// permiso quedan comentadas mientras se prueba el flujo manualmente. Reactivar antes de
// producción.
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // SALE-01: el listado vive en su propia coleccion y no colgado de una cotizacion. Un
        // pedido se sigue creando y leyendo como sub-recurso de la suya --sigue siendo 1:1-- pero
        // "los pedidos del tenant" no son de ninguna cotizacion en particular.
        var collection = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders")
            .WithTags("Orders");

        collection.MapGet("/", ListOrdersAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrdersPageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // SALE-04: por el id del pedido. Desde una fila del listado no hay por donde entrar si
        // la unica ruta cuelga de la cotizacion.
        collection.MapGet("/{orderId:guid}", GetOrderByIdAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // El listado de pedidos en un .xlsx por correo (spec 2026-09-12): mismo contrato que
        // `POST /quotations/export` —202, filtros del listado por query string, sin paginación—.
        // "export" no choca con "/{orderId:guid}": no es un guid.
        collection.MapPost("/export", ExportOrdersAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<ExportJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/{quotationId:guid}/order")
            .WithTags("Orders");

        group.MapGet("/", GetOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // US-13 a US-16: el asistente de conversión completo en un solo llamado -- estado de
        // pago, notas y comprobantes (ya subidos a Storage por fuera de este request, US-14) --
        // que aprueba la cotización y crea el pedido en la misma transacción.
        // El visto bueno de quien revisa. Ruta propia y no un campo del POST: es otra persona,
        // en otro momento -- ver ApproveOrderHandler.
        group.MapPost("/approve", ApproveOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Anular (spec 2026-09-16): desde Pending o Approved, con motivo obligatorio. Política
        // propia y no OrderManage — ver CancelOrderHandler.
        group.MapPost("/cancel", CancelOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderCancel)
            .Accepts<CancelOrderRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", ConvertQuotationToOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<ConvertQuotationToOrderRequest>("application/json")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cargar lo que faltó al convertir, lo que se terminó de cobrar después, o corregir el
        // monto de un comprobante ya cargado (a pedido, 2026-09): "Aprobar pedido" se bloquea
        // mientras el pago no está completo o correcto, y esto es la única forma de destrabarlo
        // sin recrear el pedido entero. Sólo sobre Pending — ver Order.AddPaymentProofs.
        group.MapPost("/proofs", AddOrderPaymentProofsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<AddOrderPaymentProofsRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Quitar un comprobante cargado por error (a pedido, 2026-09-15) — distinto de corregirlo
        // (eso sigue siendo POST /proofs con `updatedProofs`). Sólo sobre Pending — ver
        // Order.RemovePaymentProof.
        group.MapDelete("/proofs/{proofId:guid}", RemoveOrderPaymentProofAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // "Editar" un pedido pendiente para sumarle productos que faltaron al convertir (a
        // pedido, 2026-09) — sólo mientras Pending, ver AddOrderItemsHandler. Devuelve el pedido y
        // la cotización juntos, igual que GetOrderByIdAsync: el total nuevo vive en la segunda.
        group.MapPost("/items", AddOrderItemsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<AddOrderItemsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListOrdersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        Guid? advisorId = null,
        string? status = null,
        string? paymentStatus = null,
        DateOnly? convertedFrom = null,
        DateOnly? convertedTo = null,
        string? clientCuc = null,
        string? orderNumber = null,
        int page = 1,
        int pageSize = QuotationPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListOrdersQuery(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, orderNumber, page, pageSize),
            cancellationToken);

        return Results.Ok(new OrdersPageResponse(
            result.Items.Select(ToListItemResponse).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> ExportOrdersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        Guid? advisorId = null,
        string? status = null,
        string? paymentStatus = null,
        DateOnly? convertedFrom = null,
        DateOnly? convertedTo = null,
        string? clientCuc = null,
        string? orderNumber = null)
    {
        var accepted = await dispatcher.SendAsync(
            new ExportOrdersCommand(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, orderNumber),
            cancellationToken);

        return Results.Accepted(value: new ExportJobAcceptedResponse(accepted.JobId, accepted.RequestedAt));
    }

    private static async Task<IResult> GetOrderByIdAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.QueryAsync(
            new GetOrderByIdQuery(tenantId, orderId), cancellationToken);

        // La cotizacion se compone igual que en su propio detalle: el mismo composer, para que
        // las dos pantallas no puedan mostrar cosas distintas de la misma cotizacion.
        return Results.Ok(new OrderDetailResponse(
            ToResponse(detail.Order),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }

    private static OrderListItemResponse ToListItemResponse(OrderListItemDto order) => new(
        order.Id,
        order.OrderNumber,
        order.QuotationId,
        order.QuotationNumber,
        order.ClientId,
        order.ClientName,
        order.AdvisorId,
        order.AdvisorName,
        order.Status,
        order.PaymentStatus,
        order.PaymentMethod,
        order.ConvertedAt,
        order.Currency,
        order.Total);

    private static async Task<IResult> GetOrderAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.QueryAsync(
            new GetOrderQuery(tenantId, quotationId), cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> ConvertQuotationToOrderAsync(
        Guid tenantId,
        Guid quotationId,
        ConvertQuotationToOrderRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new ConvertQuotationToOrderCommand(
                tenantId, quotationId, request.PaymentStatus, request.Notes, request.PaymentProofs),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotationId}/order",
            ToResponse(order));
    }

    private static async Task<IResult> AddOrderPaymentProofsAsync(
        Guid tenantId,
        Guid quotationId,
        AddOrderPaymentProofsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new AddOrderPaymentProofsCommand(
                tenantId,
                quotationId,
                request.PaymentStatus,
                request.Notes,
                request.PaymentProofs,
                request.UpdatedProofs ?? []),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> RemoveOrderPaymentProofAsync(
        Guid tenantId,
        Guid quotationId,
        Guid proofId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new RemoveOrderPaymentProofCommand(tenantId, quotationId, proofId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> AddOrderItemsAsync(
        Guid tenantId,
        Guid quotationId,
        AddOrderItemsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var toAdd = request.ToAdd
            .Select(item => new OrderItemAddition(item.ProductId, item.Quantity))
            .ToArray();

        var result = await dispatcher.SendAsync(
            new AddOrderItemsCommand(tenantId, quotationId, toAdd),
            cancellationToken);

        // Misma composición que GetOrderByIdAsync: el mismo composer, para que las dos pantallas
        // no puedan mostrar cosas distintas de la misma cotización.
        return Results.Ok(new OrderDetailResponse(
            ToResponse(result.Order),
            await composer.ComposeAsync(tenantId, result.Quotation, cancellationToken)));
    }

    private static async Task<IResult> ApproveOrderAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new ApproveOrderCommand(tenantId, quotationId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> CancelOrderAsync(
        Guid tenantId,
        Guid quotationId,
        CancelOrderRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new CancelOrderCommand(tenantId, quotationId, request.Reason),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static OrderResponse ToResponse(OrderDto order) => new(
        order.Id,
        order.OrderNumber,
        order.QuotationId,
        order.Status,
        order.PaymentStatus,
        order.Notes,
        order.ConvertedAt,
        order.ConvertedBy,
        order.ApprovedAt,
        order.ApprovedBy,
        order.CancelledAt,
        order.CancelledBy,
        order.CancellationReason,
        order.RitualCollectionSyncId,
        order.CreatedAt,
        order.UpdatedAt,
        order.Version,
        order.PaymentProofs
            .Select(proof => new OrderPaymentProofResponse(
                proof.Id, proof.FileId, proof.Amount, proof.UploadedAt))
            .ToArray());
}
