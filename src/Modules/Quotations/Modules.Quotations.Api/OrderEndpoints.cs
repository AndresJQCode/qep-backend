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
        //
        // Y desde 2026-09-17 todo lo que se le *hace* al pedido cuelga de este grupo, por su
        // propio id: quien llega desde el listado tiene el id del pedido y no el de su cotización.
        // De la cotización quedan sólo convertir y leer su pedido, los dos casos en los que
        // todavía no hay un orderId que usar.
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

        // Guardar «Editar pedido» de una vez (spec 2026-09-17): el estado deseado completo, con
        // If-Match de Order.Version. Mismo contrato de precondición que PATCH /roles: sin If-Match
        // 428, versión vieja 412. Responde el detalle compuesto, igual que GetOrderByIdAsync.
        //
        // Por el id del pedido y no colgado de su cotización: lo que se edita es el pedido, y quien
        // llega desde el listado tiene su id, no el de la cotización.
        collection.MapPut("/{orderId:guid}", SaveOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cálculo previo (spec 2026-09-17, decisión 3): mismo cuerpo que el PUT, sin archivos y sin
        // persistir. POST y no GET porque lleva el borrador entero en el cuerpo.
        collection.MapPost("/{orderId:guid}/preview", PreviewOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // US-13 a US-16: el visto bueno de quien revisa. Ruta propia y no un campo del POST de
        // conversión: es otra persona, en otro momento -- ver ApproveOrderHandler.
        collection.MapPost("/{orderId:guid}/approve", ApproveOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Anular (spec 2026-09-16): desde Pending o Approved, con motivo obligatorio. Política
        // propia y no OrderManage — ver CancelOrderHandler.
        collection.MapPost("/{orderId:guid}/cancel", CancelOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderCancel)
            .Accepts<CancelOrderRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cargar lo que faltó al convertir, lo que se terminó de cobrar después, o corregir el
        // monto de un comprobante ya cargado (a pedido, 2026-09): "Aprobar pedido" se bloquea
        // mientras el pago no está completo o correcto, y esto es la única forma de destrabarlo
        // sin recrear el pedido entero. Sólo sobre Pending — ver Order.AddPaymentProofs.
        collection.MapPost("/{orderId:guid}/proofs", AddOrderPaymentProofsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<AddOrderPaymentProofsRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Quitar un comprobante cargado por error (a pedido, 2026-09-15) — distinto de corregirlo
        // (eso sigue siendo POST /proofs con `updatedProofs`). Sólo sobre Pending — ver
        // Order.RemovePaymentProof.
        collection.MapDelete("/{orderId:guid}/proofs/{proofId:guid}", RemoveOrderPaymentProofAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // "Editar" un pedido pendiente para sumarle productos que faltaron al convertir (a
        // pedido, 2026-09) — sólo mientras Pending, ver AddOrderItemsHandler. Devuelve el pedido y
        // la cotización juntos, igual que GetOrderByIdAsync: el total nuevo vive en la segunda.
        collection.MapPost("/{orderId:guid}/items", AddOrderItemsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<AddOrderItemsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
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
        group.MapPost("/", ConvertQuotationToOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<ConvertQuotationToOrderRequest>("application/json")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
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

        // El pedido tiene id propio desde SALE-01: el Location apunta a su recurso canónico bajo
        // /orders, no a la cotización que lo originó.
        return Results.Created(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}",
            ToResponse(order));
    }

    private static async Task<IResult> AddOrderPaymentProofsAsync(
        Guid tenantId,
        Guid orderId,
        AddOrderPaymentProofsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new AddOrderPaymentProofsCommand(
                tenantId,
                orderId,
                request.PaymentStatus,
                request.Notes,
                request.PaymentProofs,
                request.UpdatedProofs ?? []),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> RemoveOrderPaymentProofAsync(
        Guid tenantId,
        Guid orderId,
        Guid proofId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new RemoveOrderPaymentProofCommand(tenantId, orderId, proofId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> AddOrderItemsAsync(
        Guid tenantId,
        Guid orderId,
        AddOrderItemsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var toAdd = request.ToAdd
            .Select(item => new OrderItemAddition(item.ProductId, item.Quantity))
            .ToArray();

        var result = await dispatcher.SendAsync(
            new AddOrderItemsCommand(tenantId, orderId, toAdd),
            cancellationToken);

        // Misma composición que GetOrderByIdAsync: el mismo composer, para que las dos pantallas
        // no puedan mostrar cosas distintas de la misma cotización.
        return Results.Ok(new OrderDetailResponse(
            ToResponse(result.Order),
            await composer.ComposeAsync(tenantId, result.Quotation, cancellationToken)));
    }

    private static async Task<IResult> SaveOrderEditsAsync(
        Guid tenantId,
        Guid orderId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded order version is required.");
        }

        var (items, proofs) = ToEdits(request);
        var detail = await dispatcher.SendAsync(
            new SaveOrderEditsCommand(tenantId, orderId, expectedVersion, items, proofs, request.Notes),
            cancellationToken);

        httpContext.Response.Headers.ETag = $"\"{detail.Order.Version}\"";
        return Results.Ok(new OrderDetailResponse(
            ToResponse(detail.Order),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }

    private static async Task<IResult> PreviewOrderEditsAsync(
        Guid tenantId,
        Guid orderId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var (items, proofs) = ToEdits(request);
        var detail = await dispatcher.QueryAsync(
            new PreviewOrderEditsQuery(tenantId, orderId, items, proofs, request.Notes),
            cancellationToken);

        return Results.Ok(new OrderDetailResponse(
            ToResponse(detail.Order),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }

    // Ausentes o null equivalen a vacíos: así el validador y los handlers nunca ven colecciones null.
    private static (IReadOnlyList<OrderItemAddition> Items, OrderEditProofs Proofs) ToEdits(
        SaveOrderEditsRequest request) =>
        (
            (request.Items ?? [])
                .Select(item => new OrderItemAddition(item.ProductId, item.Quantity))
                .ToArray(),
            new OrderEditProofs(
                (request.Proofs?.Add ?? [])
                    .Select(addition => new OrderEditProofAddition(addition.FileId, addition.Amount))
                    .ToArray(),
                request.Proofs?.Update ?? [],
                request.Proofs?.RemoveIds ?? []));

    private static async Task<IResult> ApproveOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new ApproveOrderCommand(tenantId, orderId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> CancelOrderAsync(
        Guid tenantId,
        Guid orderId,
        CancelOrderRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new CancelOrderCommand(tenantId, orderId, request.Reason),
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

    // Copia de RoleEndpoints.TryParseVersion (src/Api): este proyecto no puede referenciar Api, y
    // TenantSettingsEndpoints y MembershipEndpoints ya llevan la suya. Acepta "3", 3 y W/"3".
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}
