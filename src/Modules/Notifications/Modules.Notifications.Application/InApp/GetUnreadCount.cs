using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

public sealed record GetUnreadCountQuery(Guid TenantId) : IQuery<UnreadCountDto>;

/// <summary>Spec 2026-11-22 §8.2: what the badge asks for on load and whenever it is told something changed.</summary>
public sealed class GetUnreadCountHandler(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    IInAppNotificationRepository repository)
    : IQueryHandler<GetUnreadCountQuery, UnreadCountDto>
{
    public async Task<UnreadCountDto> HandleAsync(GetUnreadCountQuery query, CancellationToken cancellationToken)
    {
        var memberId = await InAppAuthorization.EnsureMemberAsync(executionContext, memberships, query.TenantId, cancellationToken);
        return new UnreadCountDto(await repository.CountUnreadAsync(query.TenantId, memberId, cancellationToken));
    }
}
