using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace Bootstrapper.Realtime;

/// <summary>
/// Delivers what the listener receives through <c>NOTIFY</c> to this pod's connections. Any kind but <c>presenceChanged</c> goes to
/// the SignalR group of the topic, with the payload as is. <c>presenceChanged</c> carries every present member, and plan N9 sends
/// each connection the list without its own member (the SPA does not know its <c>memberId</c>), so it goes connection by connection,
/// grouped by member, from <see cref="AppHubConnections"/>. It remembers the last non-empty photo it delivered per group, so
/// <see cref="RealtimePresenceSweeper"/> can notice an entry that expired without anyone clearing it (plan N10).
/// </summary>
internal sealed class AppHubDispatcher(IHubContext<AppHub> hub, AppHubConnections connections)
{
    private readonly ConcurrentDictionary<(Guid TenantId, string Topic), IReadOnlyList<PresenceMember>> _lastDelivered = new();

    /// <summary>The groups whose last photo delivered by this pod was not empty: the only ones an expiry can change.</summary>
    public IReadOnlyList<(Guid TenantId, string Topic, IReadOnlyList<PresenceMember> Members)> PresenceToWatch() =>
        [.. _lastDelivered.Select(pair => (pair.Key.TenantId, pair.Key.Topic, pair.Value))];

    public async Task DispatchAsync(RealtimeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.MemberId is { } memberId)
        {
            await hub.Clients.Group(RealtimeTopics.MemberGroupOf(envelope.TenantId, memberId))
                .SendAsync(envelope.Kind, envelope.Payload, cancellationToken);
            return;
        }

        if (!string.Equals(envelope.Kind, AppHubEvents.PresenceChanged, StringComparison.Ordinal))
        {
            await hub.Clients.Group(RealtimeTopics.GroupOf(envelope.TenantId, envelope.Topic))
                .SendAsync(envelope.Kind, envelope.Payload, cancellationToken);
            return;
        }

        var members = PresenceMembersOf(envelope.Payload);
        await DeliverPresenceAsync(envelope.TenantId, envelope.Topic, members, cancellationToken);
    }

    /// <summary>Sends <paramref name="members"/> to every connection of this pod subscribed to the topic, each without its own member.</summary>
    public async Task DeliverPresenceAsync(
        Guid tenantId, string topic, IReadOnlyList<PresenceMember> members, CancellationToken cancellationToken)
    {
        var subscribers = connections.SubscribersOf(tenantId, topic);
        Remember(tenantId, topic, subscribers.Count > 0 ? members : []);
        foreach (var group in subscribers.GroupBy(subscriber => subscriber.MemberId))
        {
            var others = members.Where(member => member.MemberId != group.Key).ToList();
            await hub.Clients.Clients(group.Select(subscriber => subscriber.ConnectionId).ToList())
                .SendAsync(AppHubEvents.PresenceChanged, new PresencePayload(topic, others), cancellationToken);
        }
    }

    /// <summary>Sends the current snapshot to one connection only (plan N6: the photo on subscribe).</summary>
    public Task SendPresenceToAsync(
        string connectionId, Guid tenantId, Guid ownMemberId, string topic, IReadOnlyList<PresenceMember> members,
        CancellationToken cancellationToken)
    {
        // The photo may come from writers on other pods that this pod never delivered: remember it so the sweeper watches it too.
        if (members.Count > 0)
        {
            Remember(tenantId, topic, members);
        }

        return hub.Clients.Client(connectionId).SendAsync(
            AppHubEvents.PresenceChanged,
            new PresencePayload(topic, [.. members.Where(member => member.MemberId != ownMemberId)]),
            cancellationToken);
    }

    // Only non-empty photos are kept: an empty one has nothing left to expire, and dropping it bounds the memory of the pod.
    private void Remember(Guid tenantId, string topic, IReadOnlyList<PresenceMember> members)
    {
        if (members.Count == 0)
        {
            _lastDelivered.TryRemove((tenantId, topic), out _);
        }
        else
        {
            _lastDelivered[(tenantId, topic)] = members;
        }
    }

    private static List<PresenceMember> PresenceMembersOf(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("members", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.Deserialize<List<PresenceMember>>(RealtimeEventJson.Options) ?? [];
    }
}

/// <summary>The <c>presenceChanged</c> payload of the screen contract: <c>{ topic, members: [{ memberId, name, state }] }</c>.</summary>
internal sealed record PresencePayload(string Topic, IReadOnlyList<PresenceMember> Members);

internal sealed record PresenceMember(Guid MemberId, string Name, string State);
