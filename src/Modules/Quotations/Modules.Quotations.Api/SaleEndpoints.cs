using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;

namespace Modules.Quotations.Api;

// TEMPORAL (a pedido, 2026-08-24): mismo interruptor que QuotationEndpoints -- las políticas por
// permiso quedan comentadas mientras se prueba el flujo manualmente. Reactivar antes de
// producción.
public static class SaleEndpoints
{
    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // SALE-01: el listado vive en su propia coleccion y no colgado de una cotizacion. Una
        // venta se sigue creando y leyendo como sub-recurso de la suya --sigue siendo 1:1-- pero
        // "las ventas del tenant" no son de ninguna cotizacion en particular.
        var collection = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/sales")
            .WithTags("Sales");

        collection.MapGet("/", ListSalesAsync)
            .RequireAuthorization(SalesPermissions.SaleRead)
            .Produces<SalesPageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // El panel del listado. Ruta hermana y no un campo del listado: es de todo el periodo,
        // no de la pagina que se esta mirando, y se pide una vez aunque la grilla pagine.
        // Antes de "/{saleId:guid}" no hace falta desempatar: "summary" no es un guid.
        collection.MapGet("/summary", GetSalesSummaryAsync)
            .RequireAuthorization(SalesPermissions.SaleRead)
            .Produces<SaleSummaryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // SALE-04: por el id de la venta. Desde una fila del listado no hay por donde entrar si
        // la unica ruta cuelga de la cotizacion.
        collection.MapGet("/{saleId:guid}", GetSaleByIdAsync)
            .RequireAuthorization(SalesPermissions.SaleRead)
            .Produces<SaleDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/{quotationId:guid}/sale")
            .WithTags("Sales");

        group.MapGet("/", GetSaleAsync)
            .RequireAuthorization(SalesPermissions.SaleRead)
            .Produces<SaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // US-13 a US-16: el asistente de conversión completo en un solo llamado -- estado de
        // pago, notas y comprobantes (ya subidos a Storage por fuera de este request, US-14) --
        // que aprueba la cotización y crea la venta en la misma transacción.
        // El visto bueno de quien revisa. Ruta propia y no un campo del POST: es otra persona,
        // en otro momento -- ver ApproveSaleHandler.
        group.MapPost("/approve", ApproveSaleAsync)
            .RequireAuthorization(SalesPermissions.SaleManage)
            .Produces<SaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", ConvertQuotationToSaleAsync)
            .RequireAuthorization(SalesPermissions.SaleManage)
            .Accepts<ConvertQuotationToSaleRequest>("application/json")
            .Produces<SaleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetSalesSummaryAsync(
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var summary = await dispatcher.QueryAsync(
            new GetSalesSummaryQuery(tenantId, from, to), cancellationToken);

        return Results.Ok(new SaleSummaryResponse(
            summary.SaleCount,
            summary.Total,
            summary.PendingCount,
            summary.PendingTotal,
            summary.ApprovedCount,
            summary.ApprovedTotal,
            summary.CollectedTotal,
            summary.PreviousTotal));
    }

    private static async Task<IResult> ListSalesAsync(
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
        string? saleNumber = null,
        int page = 1,
        int pageSize = QuotationPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListSalesQuery(
                tenantId, clientId, advisorId, status, paymentStatus, convertedFrom, convertedTo,
                clientCuc, saleNumber, page, pageSize),
            cancellationToken);

        return Results.Ok(new SalesPageResponse(
            result.Items.Select(ToListItemResponse).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> GetSaleByIdAsync(
        Guid tenantId,
        Guid saleId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var detail = await dispatcher.QueryAsync(
            new GetSaleByIdQuery(tenantId, saleId), cancellationToken);

        // La cotizacion se compone igual que en su propio detalle: el mismo composer, para que
        // las dos pantallas no puedan mostrar cosas distintas de la misma cotizacion.
        return Results.Ok(new SaleDetailResponse(
            ToResponse(detail.Sale),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }

    private static SaleListItemResponse ToListItemResponse(SaleListItemDto sale) => new(
        sale.Id,
        sale.SaleNumber,
        sale.QuotationId,
        sale.QuotationNumber,
        sale.ClientId,
        sale.ClientName,
        sale.AdvisorId,
        sale.AdvisorEmail,
        sale.Status,
        sale.PaymentStatus,
        sale.PaymentMethod,
        sale.ConvertedAt,
        sale.Currency,
        sale.Total);

    private static async Task<IResult> GetSaleAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var sale = await dispatcher.QueryAsync(
            new GetSaleQuery(tenantId, quotationId), cancellationToken);

        return Results.Ok(ToResponse(sale));
    }

    private static async Task<IResult> ConvertQuotationToSaleAsync(
        Guid tenantId,
        Guid quotationId,
        ConvertQuotationToSaleRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var sale = await dispatcher.SendAsync(
            new ConvertQuotationToSaleCommand(
                tenantId, quotationId, request.PaymentStatus, request.Notes, request.PaymentProofs),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotationId}/sale",
            ToResponse(sale));
    }

    private static async Task<IResult> ApproveSaleAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var sale = await dispatcher.SendAsync(
            new ApproveSaleCommand(tenantId, quotationId),
            cancellationToken);

        return Results.Ok(ToResponse(sale));
    }

    private static SaleResponse ToResponse(SaleDto sale) => new(
        sale.Id,
        sale.SaleNumber,
        sale.QuotationId,
        sale.Status,
        sale.PaymentStatus,
        sale.Notes,
        sale.ConvertedAt,
        sale.ConvertedBy,
        sale.ApprovedAt,
        sale.ApprovedBy,
        sale.RitualCollectionSyncId,
        sale.CreatedAt,
        sale.UpdatedAt,
        sale.PaymentProofs
            .Select(proof => new SalePaymentProofResponse(
                proof.Id, proof.FileId, proof.Amount, proof.UploadedAt))
            .ToArray());
}
