using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace Bootstrapper.Realtime;

/// <summary>
/// Delivers what the listener receives through <c>NOTIFY</c> to this pod's connections. Any kind but <c>presenceChanged</c> goes to
/// the SignalR group of the topic, with the payload as is. <c>presenceChanged</c> carries every present member, and plan N9 sends
/// each connection the list without its own member (the SPA does not know its <c>memberId</c>), so it goes connection by connection,
/// grouped by member, from <see cref="AppHubConnections"/>.
/// </summary>
internal sealed class AppHubDispatcher(IHubContext<AppHub> hub, AppHubConnections connections)
{
    public async Task DispatchAsync(RealtimeEnvelope envelope, CancellationToken cancellationToken)
    {
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
        foreach (var group in connections.SubscribersOf(tenantId, topic).GroupBy(subscriber => subscriber.MemberId))
        {
            var others = members.Where(member => member.MemberId != group.Key).ToList();
            await hub.Clients.Clients(group.Select(subscriber => subscriber.ConnectionId).ToList())
                .SendAsync(AppHubEvents.PresenceChanged, new PresencePayload(topic, others), cancellationToken);
        }
    }

    /// <summary>Sends the current snapshot to one connection only (plan N6: the photo on subscribe).</summary>
    public Task SendPresenceToAsync(
        string connectionId, Guid ownMemberId, string topic, IReadOnlyList<PresenceMember> members, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(
            AppHubEvents.PresenceChanged,
            new PresencePayload(topic, [.. members.Where(member => member.MemberId != ownMemberId)]),
            cancellationToken);

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
