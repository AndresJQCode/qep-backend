using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Api.Realtime;

/// <summary>
/// Spec 2026-11-22 §9.2: who may listen to the <c>messaging</c> topic of the app hub (the alert of an inbound message nobody has taken).
/// The same rule as <c>MessagingHub</c> and its endpoint (<c>RequireAuthorization(MessagingPermissions.ConversationRead)</c> with the
/// <c>messaging</c> module on), but resolved from the database for the connection's member, because the hub has no request claims for a
/// <c>Subscribe</c> sent long after the handshake. Only the exact topic exists; presence is never offered here.
/// </summary>
internal sealed class MessagingRealtimeAuthorizer(
    ITenantModules tenantModules,
    IMessagingMemberPermissions permissions) : IRealtimeTopicAuthorizer
{
    private static readonly string[] ReadPermissions = [MessagingPermissions.ConversationRead];

    public string Prefix => MessagingRealtimeTopics.Prefix;

    public string ForbiddenCode => MessagingErrorCodes.RealtimeTopicForbidden;

    public async Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken)
    {
        if (!string.Equals(topic, MessagingRealtimeTopics.Inbox, StringComparison.Ordinal))
        {
            return false;
        }

        // The same rule as TenantModuleGuard: a tenant without a row (development stub) cannot be denied by module.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Messaging))
        {
            return false;
        }

        return await permissions.HasAnyAsync(tenantId, memberId, ReadPermissions, cancellationToken);
    }

    /// <summary>Presence says who is looking at a ticket; Messaging has its own (<c>MessagingHub</c>) and the alert topic has no «who».</summary>
    public Task<bool> CanSetPresenceAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public static class MessagingRealtimeAuthorizerExtensions
{
    /// <summary>Scoped, like the hub invocation that resolves it: it reads the module set and the roles per call.</summary>
    public static IServiceCollection AddMessagingRealtimeAuthorizer(this IServiceCollection services)
    {
        services.AddScoped<IRealtimeTopicAuthorizer, MessagingRealtimeAuthorizer>();
        return services;
    }
}
