using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// Pedidos (US-13 a US-17 y spec 2026-10-15). Todo lo que se le hace a un pedido va por su propio id;
/// sólo convertir y leer el pedido de una cotización cuelgan de ella. El detalle responde el pedido
/// con sus propios datos (<see cref="OrderDetailResponse"/>): la cotización ya no viaja.
/// </summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var collection = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders")
            .WithTags("Orders");

        collection.MapGet("/", ListOrdersAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrdersPageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-15 §5.2: un pedido a mano, sin cotización, y su cálculo previo con el mismo cuerpo.
        collection.MapPost("/", CreateOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderDetailResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/preview", PreviewOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapGet("/{orderId:guid}", GetOrderByIdAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        collection.MapPost("/export", ExportOrdersAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<ExportJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPut("/{orderId:guid}", SaveOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/preview", PreviewOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec de Shopify D-S25: con rememberName el handler exige además tenancy.settings.update.
        collection.MapPost("/{orderId:guid}/carrier-recognition", RecognizeOrderCarrierAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<RecognizeOrderCarrierRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/approve", ApproveOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderApprove)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/cancel", CancelOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderCancel)
            .Accepts<CancelOrderRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/invoice", InvoiceOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderInvoice)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/uninvoice", RevertOrderInvoicingAsync)
            .RequireAuthorization(OrdersPermissions.OrderInvoice)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // «Enviar el pedido por WhatsApp». Mismo contrato que POST /quotations/{id}/send: el cuerpo es
        // opcional de verdad (un cuerpo inferido como requerido rompió aquel envío con 500), y responde el
        // detalle entero para que la pantalla repinte el historial con la entrada nueva.
        collection.MapPost("/{orderId:guid}/send", SendOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SendOrderRequest>(isOptional: true, "application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapPost("/{orderId:guid}/proofs", AddOrderPaymentProofsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<AddOrderPaymentProofsRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        collection.MapDelete("/{orderId:guid}/proofs/{proofId:guid}", RemoveOrderPaymentProofAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

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

        // Se conserva por compatibilidad (plan 2026-10-15, D-O7): responde el mismo detalle que
        // GET /orders/{id}.
        group.MapGet("/", GetOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderRead)
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", ConvertQuotationToOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<ConvertQuotationToOrderRequest>("application/json")
            .Produces<OrderDetailResponse>(StatusCodes.Status201Created)
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
        string? source = null,
        int page = 1,
        int pageSize = QuotationPaging.DefaultPageSize,
        string? timeZone = null)
    {
        var result = await dispatcher.QueryAsync(
            new ListOrdersQuery(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, orderNumber, source, page, pageSize, timeZone),
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
        string? orderNumber = null,
        string? source = null,
        string? timeZone = null)
    {
        var accepted = await dispatcher.SendAsync(
            new ExportOrdersCommand(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, orderNumber, source, timeZone),
            cancellationToken);
        return Results.Accepted(value: new ExportJobAcceptedResponse(accepted.JobId, accepted.RequestedAt));
    }

    private static async Task<IResult> GetOrderByIdAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.QueryAsync(
            new GetOrderByIdQuery(tenantId, orderId), cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
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
        order.Total,
        order.Source,
        order.ExternalReference,
        ShippingResponse.From(order.Shipping));

    private static async Task<IResult> GetOrderAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.QueryAsync(
            new GetOrderQuery(tenantId, quotationId), cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static async Task<IResult> ConvertQuotationToOrderAsync(
        Guid tenantId,
        Guid quotationId,
        ConvertQuotationToOrderRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        // Spec 2026-10-15 §8: responde el detalle, no sólo el pedido. La pantalla de conversión
        // dibuja el resumen (líneas, cliente, cotización de origen) sin pedir nada después.
        var detail = await dispatcher.SendAsync(
            new ConvertQuotationToOrderCommand(
                tenantId, quotationId, request.PaymentStatus, request.Notes, request.PaymentProofs),
            cancellationToken);
        return Results.Created(
            $"/api/v1/tenants/{tenantId}/orders/{detail.Order.Id}",
            await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    /// <summary>El cuerpo es <b>opcional</b>, como en el envío de la cotización: de ahí el parámetro
    /// anulable con default (ver <c>SendWorksWithoutABody</c>).</summary>
    private static async Task<IResult> SendOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken,
        SendOrderRequest? request = null)
    {
        var detail = await dispatcher.SendAsync(
            new SendOrderCommand(tenantId, orderId, request?.Recipient),
            cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
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
        return Results.Ok(OrderResponses.ToResponse(order));
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
        return Results.Ok(OrderResponses.ToResponse(order));
    }

    private static async Task<IResult> CreateOrderAsync(
        Guid tenantId,
        CreateOrderRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.SendAsync(
            new CreateOrderCommand(
                tenantId, request.ClientId, request.PaymentMethod, request.Notes, request.IsRetail,
                request.GlobalScaleFloor, request.Parties, request.BillingAccount, ToAdditions(request.Items),
                request.PaymentStatus, request.PaymentProofs, request.Shipping),
            cancellationToken);
        return Results.Created(
            $"/api/v1/tenants/{tenantId}/orders/{detail.Order.Id}",
            await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static async Task<IResult> PreviewOrderAsync(
        Guid tenantId,
        CreateOrderRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.QueryAsync(
            new PreviewOrderQuery(
                tenantId, request.ClientId, request.PaymentMethod, request.Notes, request.IsRetail,
                request.GlobalScaleFloor, request.Parties, request.BillingAccount, ToAdditions(request.Items),
                request.PaymentStatus, request.PaymentProofs, request.Shipping),
            cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static OrderItemAddition[] ToAdditions(IReadOnlyList<OrderEditItemRequest>? items) =>
        (items ?? [])
            .Select(item => new OrderItemAddition(item.VariantId, item.Quantity))
            .ToArray();

    private static async Task<IResult> AddOrderItemsAsync(
        Guid tenantId,
        Guid orderId,
        AddOrderItemsRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var toAdd = request.ToAdd
            .Select(item => new OrderItemAddition(item.VariantId, item.Quantity))
            .ToArray();
        var detail = await dispatcher.SendAsync(
            new AddOrderItemsCommand(tenantId, orderId, toAdd),
            cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static async Task<IResult> SaveOrderEditsAsync(
        Guid tenantId,
        Guid orderId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
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
            new SaveOrderEditsCommand(
                tenantId, orderId, expectedVersion, items, proofs, request.Notes, request.GlobalScaleFloor,
                request.IsRetail, request.Shipping, request.Billing),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{detail.Order.Version}\"";
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static async Task<IResult> PreviewOrderEditsAsync(
        Guid tenantId,
        Guid orderId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var (items, proofs) = ToEdits(request);
        var detail = await dispatcher.QueryAsync(
            new PreviewOrderEditsQuery(
                tenantId, orderId, items, proofs, request.Notes, request.GlobalScaleFloor, request.IsRetail,
                request.Shipping, request.Billing),
            cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static (IReadOnlyList<OrderItemAddition> Items, OrderEditProofs Proofs) ToEdits(
        SaveOrderEditsRequest request) =>
        (
            (request.Items ?? [])
                .Select(item => new OrderItemAddition(item.VariantId, item.Quantity))
                .ToArray(),
            new OrderEditProofs(
                (request.Proofs?.Add ?? [])
                    .Select(addition => new OrderEditProofAddition(addition.FileId, addition.Amount, addition.PaidOn))
                    .ToArray(),
                request.Proofs?.Update ?? [],
                request.Proofs?.RemoveIds ?? []));

    private static async Task<IResult> RecognizeOrderCarrierAsync(
        Guid tenantId,
        Guid orderId,
        RecognizeOrderCarrierRequest request,
        IRequestDispatcher dispatcher,
        IOrderResponseComposer composer,
        CancellationToken cancellationToken)
    {
        // Responde el detalle entero, como la edición: la pantalla repinta el envío y el historial sin pedir nada.
        var detail = await dispatcher.SendAsync(
            new RecognizeOrderCarrierCommand(tenantId, orderId, request.CarrierId, request.RememberName),
            cancellationToken);
        return Results.Ok(await composer.ComposeAsync(tenantId, detail, cancellationToken));
    }

    private static async Task<IResult> ApproveOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new ApproveOrderCommand(tenantId, orderId),
            cancellationToken);
        return Results.Ok(OrderResponses.ToResponse(order));
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
        return Results.Ok(OrderResponses.ToResponse(order));
    }

    private static async Task<IResult> InvoiceOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new InvoiceOrderCommand(tenantId, orderId),
            cancellationToken);
        return Results.Ok(OrderResponses.ToResponse(order));
    }

    private static async Task<IResult> RevertOrderInvoicingAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new RevertOrderInvoicingCommand(tenantId, orderId),
            cancellationToken);
        return Results.Ok(OrderResponses.ToResponse(order));
    }

    // Acepta un ETag fuerte o débil, con o sin comillas: el frontend manda lo que recibió.
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
