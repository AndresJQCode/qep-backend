using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record GetPurchaseOrderQuery(Guid TenantId, Guid OrderId) : IQuery<PurchaseOrderDetailDto>;

public sealed class GetPurchaseOrderHandler(
    IExecutionContext executionContext,
    IPurchaseOrderRepository orders,
    PurchaseOrderComposer composer)
    : IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDetailDto>
{
    public async Task<PurchaseOrderDetailDto> HandleAsync(GetPurchaseOrderQuery query, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, query.TenantId, PurchasesPermissions.OrderRead);
        var order = await orders.GetAsync(query.TenantId, new PurchaseOrderId(query.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(query.OrderId);

        return await composer.DetailAsync(order, cancellationToken);
    }
}
