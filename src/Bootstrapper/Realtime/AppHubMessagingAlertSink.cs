using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Realtime;

/// <summary>
/// Spec 2026-11-22 §9.2 and D-9: the alert of an inbound message, written straight to this pod's connections of the app hub. It is one more
/// <see cref="IMessagingEventSink"/>: <c>MessagingEventsListener</c> runs in every pod and hands each event to all the sinks, so each pod
/// writes to its own connections. Republishing through <c>pg_notify</c> instead would multiply the alert by the number of pods.
/// With an assigned member the alert goes to that member's group, after checking what <c>MessagingHub</c> checks on connect (the module
/// is on and the member can read conversations: the personal group itself revalidates neither); without one it goes to the
/// <c>messaging</c> topic, which its authorizer already restricted when each connection subscribed. Ids only: the text never travels.
/// <c>MessagingHubEventSink</c> keeps sending <c>notify</c> to the old hub (D-9).
/// </summary>
internal sealed class AppHubMessagingAlertSink(IHubContext<AppHub> hub, IServiceScopeFactory scopes, IMemoryCache cache) : IMessagingEventSink
{
    public async Task DispatchAsync(MessagingEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!string.Equals(notification.Kind, MessagingEventKinds.InboundMessage, StringComparison.Ordinal))
        {
            // conversationChanged and any kind this build does not know: the old hub and the polling cover them.
            return;
        }

        var payload = new { conversationId = notification.ConversationId, messageId = notification.MessageId };
        if (notification.AssignedMemberId is not { } member)
        {
            await hub.Clients.Group(RealtimeTopics.GroupOf(notification.TenantId, MessagingRealtimeTopics.Inbox))
                .SendAsync(MessagingRealtimeEvents.InboundMessageAlert, payload, cancellationToken);
            return;
        }

        if (!await MemberMayBeAlertedAsync(notification.TenantId, member, cancellationToken))
        {
            return;
        }

        await hub.Clients.Group(RealtimeTopics.MemberGroupOf(notification.TenantId, member))
            .SendAsync(MessagingRealtimeEvents.InboundMessageAlert, payload, cancellationToken);
    }

    // Every pod decides for every inbound message of an assigned conversation, so the decision (module on + conversation read) is kept
    // for a short time per tenant and member, as WebhookRouting and AutomationsModuleStatus do. The alert carries ids only, so a
    // permission change taking up to this long to reach the bell exposes nothing.
    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(30);

    internal static string DecisionCacheKey(Guid tenantId, Guid memberId) => $"app-hub:messaging-alert:{tenantId}:{memberId}";

    private Task<bool> MemberMayBeAlertedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(DecisionCacheKey(tenantId, memberId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = DecisionTtl;
            return DecideAsync(tenantId, memberId, cancellationToken);
        });

    private async Task<bool> DecideAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        // A scope per decision: the sink is a singleton and what it reads is scoped. The same rule as TenantModuleGuard: a tenant without a
        // row (development stub) cannot be denied by module.
        await using var scope = scopes.CreateAsyncScope();
        var modules = await scope.ServiceProvider.GetRequiredService<ITenantModules>().FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Messaging))
        {
            return false;
        }

        return await scope.ServiceProvider.GetRequiredService<IMessagingMemberPermissions>()
            .HasAnyAsync(tenantId, memberId, [MessagingPermissions.ConversationRead], cancellationToken);
    }
}
