using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Api;

/// <summary>
/// Spec 2026-11-15 §5.1 (plan N4): who may listen to Support on the app hub. The same rule as reading a ticket over HTTP
/// (<c>SupportPolicies.TicketView</c>: read or manage, with the <c>support</c> module on), but resolved from the database for the
/// connection's member, because the hub has no request claims for a <c>Subscribe</c> sent long after the handshake. A ticket topic
/// also needs the ticket to belong to the connection's tenant. Presence is writing, so it needs <c>support.ticket.manage</c>.
/// </summary>
internal sealed class SupportRealtimeAuthorizer(
    ITenantModules tenantModules,
    ISupportMemberPermissions permissions,
    ISupportTicketRepository tickets) : IRealtimeTopicAuthorizer
{
    private static readonly string[] ViewPermissions = [SupportPermissions.Read, SupportPermissions.Manage];

    private static readonly string[] ManagePermissions = [SupportPermissions.Manage];

    public string Prefix => SupportRealtimeTopics.Prefix;

    public string ForbiddenCode => SupportErrorCodes.RealtimeTopicForbidden;

    public Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken) =>
        AllowsAsync(tenantId, memberId, topic, ViewPermissions, inboxAllowed: true, cancellationToken);

    /// <summary>Plan N5: presence only makes sense on one ticket, never on the inbox.</summary>
    public Task<bool> CanSetPresenceAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken) =>
        AllowsAsync(tenantId, memberId, topic, ManagePermissions, inboxAllowed: false, cancellationToken);

    private async Task<bool> AllowsAsync(
        Guid tenantId,
        Guid memberId,
        string topic,
        IReadOnlyCollection<string> required,
        bool inboxAllowed,
        CancellationToken cancellationToken)
    {
        Guid ticketId = Guid.Empty;
        var isInbox = string.Equals(topic, SupportRealtimeTopics.Inbox, StringComparison.Ordinal);
        if (!(isInbox && inboxAllowed) && !SupportRealtimeTopics.TryParseTicket(topic, out ticketId))
        {
            return false;
        }

        // The same rule as TenantModuleGuard: a tenant without a row (development stub) cannot be denied by module.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Support))
        {
            return false;
        }

        if (!await permissions.HasAnyAsync(tenantId, memberId, required, cancellationToken))
        {
            return false;
        }

        // The repository filters by tenant: a ticket of another tenant is the same as one that does not exist.
        return isInbox || await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken) is not null;
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
