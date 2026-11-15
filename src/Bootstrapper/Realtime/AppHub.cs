using System.Security.Claims;
using BuildingBlocks.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Modules.Tenancy.Application;

namespace Bootstrapper.Realtime;

/// <summary>
/// Spec 2026-11-15 §5.1: the global hub, owned by no module. The connection's tenant comes from the <c>tenant_id</c> claim and its
/// member from the active membership, exactly like <c>MessagingHub.OnConnectedAsync</c> but without a module check (each topic
/// checks its own module). The client chooses topics, never tenants: the group is always <c>{connection tenant}:{topic}</c>. Each
/// topic is authorized by the <see cref="IRealtimeTopicAuthorizer"/> of its prefix, resolved from the invocation's scope.
/// </summary>
internal sealed class AppHub : Hub
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

    public AppHub(
        IMembershipDirectory memberships,
        IEnumerable<IRealtimeTopicAuthorizer> authorizers,
        AppHubConnections connections)
    {
        _memberships = memberships;
        _authorizers = authorizers;
        _connections = connections;
    }

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
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Joins the topic's group; repeating it changes nothing. Refused with a <see cref="HubException"/> whose message is the
    /// authorizer's code.</summary>
    public async Task Subscribe(string topic)
    {
        var (tenantId, memberId) = Identity();
        var authorizer = AuthorizerOf(topic);
        if (authorizer is null)
        {
            throw new HubException(RealtimeTopics.ForbiddenCode);
        }

        if (!await authorizer.CanSubscribeAsync(tenantId, memberId, topic, Context.ConnectionAborted)
            || !_connections.TryAddTopic(Context.ConnectionId, topic))
        {
            throw new HubException(authorizer.ForbiddenCode);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeTopics.GroupOf(tenantId, topic), Context.ConnectionAborted);
    }

    /// <summary>Leaves the topic's group. Never fails, not even for a topic the connection never had.</summary>
    public async Task Unsubscribe(string topic)
    {
        if (string.IsNullOrEmpty(topic) || topic.Length > RealtimeTopics.MaxLength)
        {
            return;
        }

        var (tenantId, _) = Identity();
        _connections.RemoveTopic(Context.ConnectionId, topic);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeTopics.GroupOf(tenantId, topic), Context.ConnectionAborted);
    }

    private (Guid TenantId, Guid MemberId) Identity() =>
        Context.Items.TryGetValue(TenantItem, out var tenant) && Context.Items.TryGetValue(MemberItem, out var member)
            ? ((Guid)tenant!, (Guid)member!)
            // OnConnectedAsync aborts before any invocation can run without an identity; this only guards a broken invariant.
            : throw new HubException(RealtimeTopics.ForbiddenCode);

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
