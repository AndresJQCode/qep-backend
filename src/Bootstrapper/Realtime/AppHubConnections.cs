using System.Collections.Concurrent;

namespace Bootstrapper.Realtime;

/// <summary>
/// Plan N9 (and spec §5.3): the app hub connections of this pod, with their tenant, member and topics. SignalR groups cannot tell a
/// sender who is in them, and <c>presenceChanged</c> has to leave out each connection's own member; on disconnect the hub also needs
/// to know which topics the connection was in. Per pod by design: each pod delivers only to its own connections.
/// </summary>
internal sealed class AppHubConnections
{
    private readonly ConcurrentDictionary<string, Entry> _connections = new(StringComparer.Ordinal);

    public void Register(string connectionId, Guid tenantId, Guid memberId) =>
        _connections[connectionId] = new Entry(tenantId, memberId);

    /// <returns><c>false</c> when the connection is unknown or already has <see cref="RealtimeTopics.MaxPerConnection"/> other topics.
    /// Adding a topic it already has is a success and changes nothing.</returns>
    public bool TryAddTopic(string connectionId, string topic)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return false;
        }

        lock (entry.Topics)
        {
            if (entry.Topics.Contains(topic))
            {
                return true;
            }

            if (entry.Topics.Count >= RealtimeTopics.MaxPerConnection)
            {
                return false;
            }

            entry.Topics.Add(topic);
            return true;
        }
    }

    public bool HasTopic(string connectionId, string topic)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return false;
        }

        lock (entry.Topics)
        {
            return entry.Topics.Contains(topic);
        }
    }

    public void RemoveTopic(string connectionId, string topic)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            lock (entry.Topics)
            {
                entry.Topics.Remove(topic);
            }
        }
    }

    /// <returns>The topics the connection had, so the hub can clear what it left behind.</returns>
    public IReadOnlyList<string> Remove(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
        {
            return [];
        }

        lock (entry.Topics)
        {
            return [.. entry.Topics];
        }
    }

    public IReadOnlyList<(string ConnectionId, Guid MemberId)> SubscribersOf(Guid tenantId, string topic)
    {
        var subscribers = new List<(string ConnectionId, Guid MemberId)>();
        foreach (var (connectionId, entry) in _connections)
        {
            if (entry.TenantId != tenantId)
            {
                continue;
            }

            lock (entry.Topics)
            {
                if (entry.Topics.Contains(topic))
                {
                    subscribers.Add((connectionId, entry.MemberId));
                }
            }
        }

        return subscribers;
    }

    private sealed record Entry(Guid TenantId, Guid MemberId)
    {
        public HashSet<string> Topics { get; } = new(StringComparer.Ordinal);
    }
}
