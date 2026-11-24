using System.Security.Claims;
using BuildingBlocks.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Modules.Tenancy.Application;

namespace Bootstrapper.Realtime;

/// <summary>
/// Spec 2026-11-15 §5.1: the global hub, owned by no module. The connection's tenant comes from the <c>tenant_id</c> claim and its
/// member from the active membership, exactly like <c>MessagingHub.OnConnectedAsync</c> but without a module check (each topic
/// checks its own module). The client chooses topics, never tenants: the group is always <c>{connection tenant}:{topic}</c>. Each
/// topic is authorized by the <see cref="IRealtimeTopicAuthorizer"/> of its prefix, resolved from the invocation's scope.
///
/// Presence (§5.3) is per connection: <c>SetPresence</c> writes <c>{memberId}:{connectionId}</c>, and leaving a topic or disconnecting
/// clears only that connection's entry, so another tab of the same member keeps it present. Every change is published through
/// <see cref="IRealtimePublisher"/> so every pod delivers it to its own subscribers.
/// </summary>
internal sealed partial class AppHub : Hub
{
    // Same claim names as MessagingHub: `qep_sub` from the session cookie, `sub` from the development stub.
    private const string QepSubjectClaim = "qep_sub";
    private const string SubjectClaim = "sub";
    private const string TenantClaim = "tenant_id";

    private const string TenantItem = "app-hub:tenant";
    private const string MemberItem = "app-hub:member";

    private readonly IMembershipDirectory _memberships;
    private readonly IEnumerable<IRealtimeTopicAuthorizer> _authorizers;
    private readonly AppHubConnections _connections;
    private readonly AppHubDispatcher _dispatcher;
    private readonly IRealtimePresence _presence;
    private readonly IAppMemberPresence _memberPresence;
    private readonly IRealtimeMemberNames _names;
    private readonly IRealtimePublisher _publisher;
    private readonly ILogger<AppHub> _logger;

    public AppHub(
        IMembershipDirectory memberships,
        IEnumerable<IRealtimeTopicAuthorizer> authorizers,
        AppHubConnections connections,
        AppHubDispatcher dispatcher,
        IRealtimePresence presence,
        IAppMemberPresence memberPresence,
        IRealtimeMemberNames names,
        IRealtimePublisher publisher,
        ILogger<AppHub> logger)
    {
        _memberships = memberships;
        _authorizers = authorizers;
        _connections = connections;
        _dispatcher = dispatcher;
        _presence = presence;
        _memberPresence = memberPresence;
        _names = names;
        _publisher = publisher;
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clearing the presence of a closed connection on topic {Topic} failed.")]
    private static partial void LogDisconnectCleanupFailed(ILogger logger, string topic, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Announcing the member's app presence failed.")]
    private static partial void LogMemberPresenceFailed(ILogger logger, Exception exception);

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        var subject = user?.FindFirstValue(QepSubjectClaim) ?? user?.FindFirstValue(SubjectClaim);
        if (!Guid.TryParse(subject, out var subjectId) || !Guid.TryParse(user?.FindFirstValue(TenantClaim), out var tenantId))
        {
            Context.Abort();
            return;
        }

        var memberId = await _memberships.FindActiveMembershipIdAsync(subjectId, tenantId, Context.ConnectionAborted);
        if (memberId is null)
        {
            Context.Abort();
            return;
        }

        Context.Items[TenantItem] = tenantId;
        Context.Items[MemberItem] = memberId.Value;
        _connections.Register(Context.ConnectionId, tenantId, memberId.Value);
        try
        {
            _memberPresence.Connected(tenantId, memberId.Value);
        }
        catch (Exception failure)
        {
            // Presence only decides whether a task notice waits; it must never refuse the connection.
            LogMemberPresenceFailed(_logger, failure);
        }

        // Spec 2026-11-22 D-3: the member's own group is joined from the claims and never through Subscribe, so a reconnect rejoins it
        // by itself and no client can ask for somebody else's.
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeTopics.MemberGroupOf(tenantId, memberId.Value), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Spec §5.3: a closed tab clears its own entries, on every topic it had, and the others see it at once. Every entry is cleared
    /// before any announcement, and each announcement fails on its own: one topic whose names or publish fail must not leave the
    /// connection's entries on the other topics alive for 45 s, nor skip the base cleanup.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var topics = _connections.Remove(Context.ConnectionId);
            if (TryIdentity(out var tenantId, out var memberId))
            {
                try
                {
                    // After the removal above, so HasMember sees the member's other tabs.
                    _memberPresence.Disconnected(tenantId, memberId);
                }
                catch (Exception failure)
                {
                    LogMemberPresenceFailed(_logger, failure);
                }

                var cleared = new List<(string Topic, IReadOnlyList<Guid> Present)>();
                foreach (var topic in topics.Where(RealtimePresence.Applies))
                {
                    try
                    {
                        // On disconnect ConnectionAborted is already cancelled: the cleanup must still run to the end.
                        var present = await _presence.ClearAsync(tenantId, topic, memberId, Context.ConnectionId, CancellationToken.None);
                        cleared.Add((topic, present));
                    }
                    catch (Exception failure)
                    {
                        LogDisconnectCleanupFailed(_logger, topic, failure);
                    }
                }

                foreach (var (topic, present) in cleared)
                {
                    try
                    {
                        await AnnounceAsync(tenantId, topic, present, CancellationToken.None);
                    }
                    catch (Exception failure)
                    {
                        LogDisconnectCleanupFailed(_logger, topic, failure);
                    }
                }
            }
        }
        finally
        {
            await base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>Joins the topic's group; repeating it changes nothing. Refused with a <see cref="HubException"/> whose message is the
    /// authorizer's code. A presence topic answers at once with its photo, to this connection only (plan N6).</summary>
    public async Task Subscribe(string topic)
    {
        var (tenantId, memberId) = Identity();
        var authorizer = AuthorizerOf(topic) ?? throw new HubException(RealtimeTopics.ForbiddenCode);
        if (!await authorizer.CanSubscribeAsync(tenantId, memberId, topic, Context.ConnectionAborted)
            || !_connections.TryAddTopic(Context.ConnectionId, topic))
        {
            throw new HubException(authorizer.ForbiddenCode);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeTopics.GroupOf(tenantId, topic), Context.ConnectionAborted);
        if (RealtimePresence.Applies(topic))
        {
            var present = await _presence.ReadAsync(tenantId, topic, Context.ConnectionAborted);
            var members = await RealtimePresenceNames.ComposeAsync(_names, tenantId, present, Context.ConnectionAborted);
            await _dispatcher.SendPresenceToAsync(Context.ConnectionId, tenantId, memberId, topic, members, Context.ConnectionAborted);
        }
    }

    /// <summary>Leaves the topic's group and clears this connection's presence there. Never fails, not even for a topic the
    /// connection never had.</summary>
    public async Task Unsubscribe(string topic)
    {
        // Like Subscribe, it leaves the «@» names alone: the member's own group is not the client's to leave, or it could cut its own alerts.
        if (string.IsNullOrEmpty(topic)
            || topic.StartsWith('@')
            || topic.Length > RealtimeTopics.MaxLength
            || !TryIdentity(out var tenantId, out var memberId))
        {
            return;
        }

        var had = _connections.HasTopic(Context.ConnectionId, topic);
        _connections.RemoveTopic(Context.ConnectionId, topic);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeTopics.GroupOf(tenantId, topic), Context.ConnectionAborted);
        if (had && RealtimePresence.Applies(topic))
        {
            await ClearAndAnnounceAsync(tenantId, memberId, topic);
        }
    }

    /// <summary>
    /// Spec §5.3 and plan N5: <c>composing</c> (valid 45 s, the screen renews it every 20 s) or <c>none</c>, only on a topic this
    /// connection already subscribed to and that the authorizer lets the member write presence on. Anything else is refused with the
    /// forbidden code (owner O3: an unknown state too).
    /// </summary>
    public async Task SetPresence(string topic, string state)
    {
        var (tenantId, memberId) = Identity();
        var composing = string.Equals(state, RealtimePresence.Composing, StringComparison.Ordinal);
        if (!composing && !string.Equals(state, RealtimePresence.None, StringComparison.Ordinal))
        {
            throw new HubException(RealtimeTopics.ForbiddenCode);
        }

        var authorizer = AuthorizerOf(topic) ?? throw new HubException(RealtimeTopics.ForbiddenCode);
        if (!RealtimePresence.Applies(topic)
            || !_connections.HasTopic(Context.ConnectionId, topic)
            || !await authorizer.CanSetPresenceAsync(tenantId, memberId, topic, Context.ConnectionAborted))
        {
            throw new HubException(authorizer.ForbiddenCode);
        }

        var present = composing
            ? await _presence.SetComposingAsync(tenantId, topic, memberId, Context.ConnectionId, Context.ConnectionAborted)
            : await _presence.ClearAsync(tenantId, topic, memberId, Context.ConnectionId, Context.ConnectionAborted);
        await AnnounceAsync(tenantId, topic, present, Context.ConnectionAborted);
    }

    // Unsubscribe: a tab closing mid-call cancels ConnectionAborted, and the cleanup must still run to the end.
    private async Task ClearAndAnnounceAsync(Guid tenantId, Guid memberId, string topic)
    {
        var present = await _presence.ClearAsync(tenantId, topic, memberId, Context.ConnectionId, CancellationToken.None);
        await AnnounceAsync(tenantId, topic, present, CancellationToken.None);
    }

    // Every pod (this one included) receives it through NOTIFY and delivers it to its own subscribers without their own member.
    private async Task AnnounceAsync(Guid tenantId, string topic, IReadOnlyList<Guid> present, CancellationToken cancellationToken)
    {
        var members = await RealtimePresenceNames.ComposeAsync(_names, tenantId, present, cancellationToken);
        await _publisher.PublishAsync(
            tenantId, topic, AppHubEvents.PresenceChanged, new PresencePayload(topic, members), CancellationToken.None);
    }

    private (Guid TenantId, Guid MemberId) Identity() =>
        TryIdentity(out var tenantId, out var memberId)
            ? (tenantId, memberId)
            // OnConnectedAsync aborts before any invocation can run without an identity; this only guards a broken invariant.
            : throw new HubException(RealtimeTopics.ForbiddenCode);

    private bool TryIdentity(out Guid tenantId, out Guid memberId)
    {
        tenantId = Guid.Empty;
        memberId = Guid.Empty;
        if (Context.Items.TryGetValue(TenantItem, out var tenant) && tenant is Guid tenantValue
            && Context.Items.TryGetValue(MemberItem, out var member) && member is Guid memberValue)
        {
            tenantId = tenantValue;
            memberId = memberValue;
            return true;
        }

        return false;
    }

    private IRealtimeTopicAuthorizer? AuthorizerOf(string? topic)
    {
        if (string.IsNullOrEmpty(topic) || topic.Length > RealtimeTopics.MaxLength)
        {
            return null;
        }

        var prefix = RealtimeTopics.PrefixOf(topic);
        return _authorizers.FirstOrDefault(authorizer => string.Equals(authorizer.Prefix, prefix, StringComparison.Ordinal));
    }
}
