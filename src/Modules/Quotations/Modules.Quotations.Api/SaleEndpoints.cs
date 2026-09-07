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
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/{quotationId:guid}/sale")
            .WithTags("Sales");

        group.MapGet("/", GetSaleAsync)
            .RequireAuthorization(/* SalesPermissions.SaleRead */)
            .Produces<SaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // US-13 a US-16: el asistente de conversión completo en un solo llamado -- estado de
        // pago, notas y comprobantes (ya subidos a Storage por fuera de este request, US-14) --
        // que aprueba la cotización y crea la venta en la misma transacción.
        // El visto bueno de quien revisa. Ruta propia y no un campo del POST: es otra persona,
        // en otro momento -- ver ApproveSaleHandler.
        group.MapPost("/approve", ApproveSaleAsync)
            .RequireAuthorization(/* SalesPermissions.SaleManage */)
            .Produces<SaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", ConvertQuotationToSaleAsync)
            .RequireAuthorization(/* SalesPermissions.SaleManage */)
            .Accepts<ConvertQuotationToSaleRequest>("application/json")
            .Produces<SaleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

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
