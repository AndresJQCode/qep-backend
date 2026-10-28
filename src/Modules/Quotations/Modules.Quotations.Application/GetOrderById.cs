using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetOrderByIdQuery(Guid TenantId, Guid OrderId) : IQuery<OrderDetailDto>;

public sealed class GetOrderByIdHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrderByIdQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        GetOrderByIdQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderRead);

        var order = await orderRepository.FindUntrackedByIdAsync(
            query.TenantId, new OrderId(query.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, query.TenantId, order, cancellationToken);
    }
}

/// <summary>Arma el <see cref="OrderDetailDto"/> de un pedido ya cargado: el número de la cotización
/// de origen (si la hay) y el historial. Lo comparten el detalle y los handlers que devuelven el
/// pedido después de guardarlo.</summary>
internal static class OrderDetails
{
    public static async Task<OrderDetailDto> LoadAsync(
        IOrderRepository orders,
        IQuotationRepository quotations,
        Guid tenantId,
        Order order,
        CancellationToken cancellationToken)
    {
        var quotationNumber = order.QuotationId is { } quotationId
            ? (await quotations.FindUntrackedAsync(tenantId, quotationId, cancellationToken))?.QuotationNumber
            : null;
        var history = await orders.ListHistoryAsync(tenantId, order.Id, cancellationToken);
        return new OrderDetailDto(order.ToDto(), quotationNumber, history.Select(OrderMapping.ToDto).ToArray());
    }
}
