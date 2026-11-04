using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

public sealed record GetOrderRequestQuery(Guid TenantId, Guid RequestId) : IQuery<StoreOrderRequestDetailDto>;

public sealed class GetOrderRequestHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreOrderRequestRepository requests,
    ITenantClock tenantClock,
    OrderRequestRowComposer composer)
    : IQueryHandler<GetOrderRequestQuery, StoreOrderRequestDetailDto>
{
    public async Task<StoreOrderRequestDetailDto> HandleAsync(GetOrderRequestQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.OrderManage, cancellationToken);

        var request = await requests.FindAsync(query.TenantId, query.RequestId, cancellationToken)
            ?? throw StoreNotFound.OrderRequest(query.RequestId);
        var now = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).UtcNow;
        var row = (await composer.ComposeAsync(query.TenantId, [request], now, cancellationToken))[0];
        return new StoreOrderRequestDetailDto(row, OrderRequestRowComposer.ToLines(request));
    }
}
