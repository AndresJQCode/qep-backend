using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetOrderQuery(Guid TenantId, Guid QuotationId) : IQuery<OrderDto>;

public sealed class GetOrderHandler(
    IOrderRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<OrderDto> HandleAsync(GetOrderQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderRead);

        var order = await repository.FindByQuotationIdAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken);

        return order?.ToDto() ?? throw OrderNotFound.For(query.QuotationId);
    }
}
