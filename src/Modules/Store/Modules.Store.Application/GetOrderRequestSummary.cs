using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

public sealed record GetOrderRequestSummaryQuery(Guid TenantId) : IQuery<StoreInboxSummaryDto>;

public sealed class GetOrderRequestSummaryHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreMemberDirectory members,
    IStoreOrderRequestRepository requests,
    ITenantClock tenantClock)
    : IQueryHandler<GetOrderRequestSummaryQuery, StoreInboxSummaryDto>
{
    public async Task<StoreInboxSummaryDto> HandleAsync(GetOrderRequestSummaryQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.OrderManage, cancellationToken);

        var caller = await members.ResolveCallerAsync(query.TenantId, cancellationToken);
        // «Hoy» es el del calendario del tenant: una solicitud de las 23:30 de ayer en Bogotá no cuenta aunque ya sea hoy en UTC.
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var counts = await requests.CountAsync(
            query.TenantId,
            caller.MembershipId,
            calendar.StartOfDayUtc(calendar.Today),
            calendar.EndOfDayExclusiveUtc(calendar.Today),
            cancellationToken);
        return new StoreInboxSummaryDto(counts.Untaken, counts.TakenByMe, counts.ReceivedToday);
    }
}
