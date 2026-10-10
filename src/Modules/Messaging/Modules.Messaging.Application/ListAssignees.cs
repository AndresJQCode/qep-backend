using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListAssigneesQuery(Guid TenantId) : IQuery<AssigneesDto>;

/// <summary>Spec 2026-10-10 §5.1 (D-A1): el selector de transferir. Permiso <c>manage</c>: sólo quien puede
/// transferir necesita la lista.</summary>
public sealed class ListAssigneesHandler(IMessagingAssignees assignees, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListAssigneesQuery, AssigneesDto>
{
    public async Task<AssigneesDto> HandleAsync(ListAssigneesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        return new AssigneesDto(await assignees.ListAsync(query.TenantId, cancellationToken));
    }
}
