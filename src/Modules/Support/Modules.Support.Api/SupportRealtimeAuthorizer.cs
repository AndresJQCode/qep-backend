using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Api;

/// <summary>
/// Spec 2026-11-15 §5.1 (plan N4) and spec 2026-11-22 §5.6: who may listen to Support on the app hub. The same table as over HTTP
/// (<see cref="SupportTicketAccessPolicy"/>), but resolved from the database for the connection's member, because the hub has no
/// request claims for a <c>Subscribe</c> sent long after the handshake: a member who lost <c>support.task.use</c> loses the task even
/// if she is still its creator or assignee. The inbox is for who reads or manages. <c>support:tasks:{memberId}</c> is only the
/// connection's own member. A ticket topic needs the ticket to belong to the connection's tenant and at least the view level.
/// Presence is writing, so it needs the act level: a manager, or a participant of a task.
/// </summary>
internal sealed class SupportRealtimeAuthorizer(
    ITenantModules tenantModules,
    ISupportMemberPermissions permissions,
    ISupportTicketRepository tickets) : IRealtimeTopicAuthorizer
{
    private static readonly string[] ReadOnly = [SupportPermissions.Read];

    private static readonly string[] ManageOnly = [SupportPermissions.Manage];

    private static readonly string[] TaskUseOnly = [SupportPermissions.TaskUse];

    public string Prefix => SupportRealtimeTopics.Prefix;

    public string ForbiddenCode => SupportErrorCodes.RealtimeTopicForbidden;

    public Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken) =>
        AllowsAsync(tenantId, memberId, topic, presence: false, cancellationToken);

    /// <summary>Plan N5: presence only makes sense on one ticket, never on the inbox nor on a tasks topic.</summary>
    public Task<bool> CanSetPresenceAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken) =>
        AllowsAsync(tenantId, memberId, topic, presence: true, cancellationToken);

    private async Task<bool> AllowsAsync(
        Guid tenantId, Guid memberId, string topic, bool presence, CancellationToken cancellationToken)
    {
        var isInbox = string.Equals(topic, SupportRealtimeTopics.Inbox, StringComparison.Ordinal);
        var isTasks = SupportRealtimeTopics.TryParseTasks(topic, out var tasksOwner);
        var isTicket = SupportRealtimeTopics.TryParseTicket(topic, out var ticketId);
        if (presence ? !isTicket : !(isInbox || isTasks || isTicket))
        {
            return false;
        }

        // The same rule as TenantModuleGuard: a tenant without a row (development stub) cannot be denied by module.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Support))
        {
            return false;
        }

        var grants = new SupportGrants(
            await permissions.HasAnyAsync(tenantId, memberId, ReadOnly, cancellationToken),
            await permissions.HasAnyAsync(tenantId, memberId, ManageOnly, cancellationToken),
            await permissions.HasAnyAsync(tenantId, memberId, TaskUseOnly, cancellationToken));
        if (isInbox)
        {
            return grants.Read || grants.Manage;
        }

        if (isTasks)
        {
            // Only her own: nobody, not even a manager, listens to another member's tasks.
            return tasksOwner == memberId && (grants.TaskUse || grants.Manage || grants.Read);
        }

        // The repository filters by tenant: a ticket of another tenant is the same as one that does not exist.
        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken);
        if (ticket is null)
        {
            return false;
        }

        var level = SupportTicketAccessPolicy.For(grants, ticket, memberId);
        return presence ? level is SupportTicketAccessLevel.Act : level is not SupportTicketAccessLevel.None;
    }
}

public static class SupportRealtimeExtensions
{
    /// <summary>Scoped, like the hub invocation that resolves it: it reads the module set, the roles and the ticket per call.</summary>
    public static IServiceCollection AddSupportRealtimeAuthorizer(this IServiceCollection services)
    {
        services.AddScoped<IRealtimeTopicAuthorizer, SupportRealtimeAuthorizer>();
        return services;
    }
}
