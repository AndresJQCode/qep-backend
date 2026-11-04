using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

internal sealed class StoreOrderRequestRepository(StoreDbContext dbContext) : IStoreOrderRequestRepository
{
    public void Add(StoreOrderRequest request) => dbContext.OrderRequests.Add(request);

    // Las líneas son owned: EF las trae siempre con la solicitud, así que no hay Include que olvidar.
    public Task<StoreOrderRequest?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.OrderRequests.FirstOrDefaultAsync(
            value => value.TenantId == tenantId && value.Id == id,
            cancellationToken);

    public Task<StoreOrderRequest?> FindByClientRequestIdAsync(
        Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
        dbContext.OrderRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                value => value.TenantId == tenantId && value.ClientRequestId == clientRequestId,
                cancellationToken);

    public Task<StoreOrderRequest?> FindByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        dbContext.OrderRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.TenantId == tenantId && value.OrderId == orderId, cancellationToken);

    public async Task<(IReadOnlyList<StoreOrderRequest> Items, int Total)> ListAsync(
        StoreInboxFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.OrderRequests.AsNoTracking().Where(value => value.TenantId == filter.TenantId);
        query = filter.View switch
        {
            StoreInboxView.Untaken => query.Where(value => value.Status == StoreOrderRequestStatus.New),
            StoreInboxView.TakenByMe => query.Where(value =>
                value.Status == StoreOrderRequestStatus.Converted && value.ConvertedBy == filter.CallerMemberId),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(value => value.ReceivedAt)
            .ThenByDescending(value => value.Number)
            .Skip(filter.Skip)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<StoreInboxCounts> CountAsync(
        Guid tenantId,
        Guid callerMemberId,
        DateTimeOffset todayStartUtc,
        DateTimeOffset todayEndUtc,
        CancellationToken cancellationToken)
    {
        var requests = dbContext.OrderRequests.AsNoTracking().Where(value => value.TenantId == tenantId);

        var untaken = await requests.CountAsync(value => value.Status == StoreOrderRequestStatus.New, cancellationToken);
        var takenByMe = await requests.CountAsync(
            value => value.Status == StoreOrderRequestStatus.Converted && value.ConvertedBy == callerMemberId,
            cancellationToken);
        var receivedToday = await requests.CountAsync(
            value => value.ReceivedAt >= todayStartUtc && value.ReceivedAt < todayEndUtc,
            cancellationToken);
        return new StoreInboxCounts(untaken, takenByMe, receivedToday);
    }

    public Task<int> CountReceivedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
        dbContext.OrderRequests
            .AsNoTracking()
            .CountAsync(value => value.TenantId == tenantId && value.ReceivedAt >= since, cancellationToken);
}
