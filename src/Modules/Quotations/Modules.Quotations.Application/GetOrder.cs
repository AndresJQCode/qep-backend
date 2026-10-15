using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>El pedido de una cotización, por el id de la cotización. Se conserva por compatibilidad
/// (plan 2026-10-15, D-O7) y responde lo mismo que <see cref="GetOrderByIdQuery"/>.</summary>
public sealed record GetOrderQuery(Guid TenantId, Guid QuotationId) : IQuery<OrderDetailDto>;

public sealed class GetOrderHandler(
    IOrderRepository repository,
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrderQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(GetOrderQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderRead);

        var order = await repository.FindByQuotationIdAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(query.QuotationId);

        return await OrderDetails.LoadAsync(
            repository, quotationRepository, query.TenantId, order, cancellationToken);
    }
}
